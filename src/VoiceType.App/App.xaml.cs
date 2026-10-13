using System.IO;
using System.Windows;
using System.Windows.Threading;
using VoiceType.App.Services;
using VoiceType.Settings;
using Forms = System.Windows.Forms;

namespace VoiceType.App;

/// <summary>
/// بداية البرنامج: نسخة واحدة فقط، أيقونة بجوار الساعة، زر المايك العائم، الاختصارات، والنافذة الرئيسية عند الطلب.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "VoiceType.SingleInstance.v1";
    private const string ShowEventName = "VoiceType.ShowMainWindow.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private int? _escapeHotkey;
    private MainWindow? _main;
    private bool _exiting;

    public static AppServices Services { get; private set; } = null!;

    public static DictationController Dictation { get; private set; } = null!;

    public static MicWindow Mic { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // البرنامج يعمل بالفعل: نطلب منه إظهار نافذته ونخرج
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        Services = new AppServices();
        Sounds.Enabled = Services.Settings.Sounds;
        TextInjector.FixMixedDirection = Services.Settings.FixMixedDirection;
        TextInjector.ArabicStartsRight = Services.Settings.ArabicStartsRight;
        Dictation = new DictationController(Services);
        StartupManager.Refresh(Services.Settings.StartWithWindows);

        Mic = new MicWindow();
        ApplyMicLook();
        var settings = Services.Settings;
        if (settings.MicX is null && settings.MicLeft is { } oldLeft && settings.MicTop is { } oldTop)
        {
            // النسخ القديمة كانت تحفظ مكان النافذة (340×96 والزر 72 على يمينها) بوحدات WPF
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Mic);
            var shift = (72 - (64 * MicWindow.ScaleOf(settings.MicSize))) / 2;
            settings.MicX = (oldLeft + 340 - 8 - 72 + shift) * dpi.DpiScaleX;
            settings.MicY = (oldTop + 12 + shift) * dpi.DpiScaleY;
        }

        Mic.PlaceButton(settings.MicX, settings.MicY);
        Mic.Clicked += OnMicClicked;
        Mic.MenuRequested += ShowMicMenu;
        Mic.Moved += (x, y) =>
        {
            Services.Settings.MicX = Math.Round(x, 1);
            Services.Settings.MicY = Math.Round(y, 1);
            Services.Settings.Save();
        };

        Dictation.StateChanged += OnDictationStateChanged;
        Dictation.LevelChanged += level => Dispatcher.InvokeAsync(() => Mic.SetLevel(level));
        Dictation.LiveText += (_, interim) => Dispatcher.InvokeAsync(() =>
        {
            // الكلام الحالي يظهر بجانب زر المايك كلمة بكلمة، ويُكتب في البرنامج عند كل وقفة
            if (Mic.IsVisible && Dictation.Target == DictationTarget.ActiveApp && interim.Length > 0)
            {
                Mic.ShowLive(interim);
            }
        });
        Dictation.Notice += (text, error) => Dispatcher.InvokeAsync(() =>
        {
            if (Mic.IsVisible)
            {
                Mic.ShowBubble(text, error);
            }
            else if (error)
            {
                _tray?.ShowBalloonTip(3000, "صدى", text, Forms.ToolTipIcon.Warning);
            }
        });

        if (Services.Settings.ShowMic)
        {
            Mic.Show();
        }

        CreateTray();
        RegisterHotkeys();
        ListenForShowRequests();

        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!background || Services.Settings.Primary is null)
        {
            ShowMain(Services.Settings.Primary is null ? "models" : null);
        }
    }

    public void ShowMain(string? page = null)
    {
        _main ??= new MainWindow();
        _main.Open(page);
    }

    /// <summary>يطبق شكل زر المايك من الإعدادات (الحجم، اللون، فوق البرامج، الشفافية).</summary>
    public void ApplyMicLook()
    {
        var s = Services.Settings;
        Mic.ApplyLook(s.MicSize, s.MicColor, s.MicAlwaysOnTop, s.MicDimWhenIdle);
    }

    private bool MainIsOpen => _main is { IsVisible: true } && _main.WindowState != WindowState.Minimized;

    private void ToggleMain()
    {
        if (MainIsOpen)
        {
            _main!.Hide();
        }
        else
        {
            ShowMain();
        }
    }

    public void SetMicVisible(bool visible)
    {
        if (visible)
        {
            Mic.Show();
        }
        else
        {
            Mic.Hide();
        }

        Services.Settings.ShowMic = visible;
        Services.Settings.Save();
        UpdateTrayMenu();
        _main?.Push("mic.visible", visible);
    }

    public void ExitApp()
    {
        _exiting = true;
        Dictation.Cancel();
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _main?.CloseForExit();
        Mic.Close();
        Dictation.Dispose();
        Services.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    public bool IsExiting => _exiting;

    private async void OnMicClicked()
    {
        if (Dictation.State == DictationState.Processing)
        {
            Mic.ShowBubble("جارٍ التفريغ… لحظة", seconds: 1.5);
            return;
        }

        if (Dictation.State == DictationState.Idle && Services.Settings.Primary is null)
        {
            Mic.ShowBubble("اختر نموذجًا أولًا: نزّل نموذجًا على جهازك أو أضف مفتاحًا.", error: true);
            ShowMain("models");
            return;
        }

        await Dictation.ToggleAsync(Dictation.State == DictationState.Recording ? Dictation.Target : DictationTarget.ActiveApp);
    }

    private void OnDictationStateChanged(DictationState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Mic.SetState(state);
            _main?.Push("dictation.state", state.ToString());

            // Esc يلغي التسجيل، ويُسجَّل فقط أثناء التسجيل حتى لا نأخذه من البرامج الأخرى
            if (state == DictationState.Recording && _escapeHotkey is null)
            {
                _escapeHotkey = _hotkeys?.Register(0, NativeMethods.VK_ESCAPE, () => Dictation.Cancel());
            }
            else if (state != DictationState.Recording && _escapeHotkey is not null)
            {
                _hotkeys?.Unregister(_escapeHotkey);
                _escapeHotkey = null;
            }
        });
    }

    private void RegisterHotkeys()
    {
        _hotkeys = new HotkeyManager();
        var id = _hotkeys.Register(
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
            NativeMethods.VK_M,
            () => SetMicVisible(!Mic.IsVisible));
        if (id is null)
        {
            _tray?.ShowBalloonTip(4000, "صدى", "الاختصار Ctrl+Alt+Shift+M مستخدم من برنامج آخر. يمكنك إظهار الزر من الأيقونة بجوار الساعة.", Forms.ToolTipIcon.Info);
        }
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/voicetype.ico"))?.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
            Text = "صدى — انقر مرتين للفتح",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes },
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.ContextMenuStrip.Opening += (_, _) =>
        {
            UpdateTrayMenu();
            Mic.MenuOpen = true; // لا نرفع زر المايك فوق القائمة
        };
        _tray.ContextMenuStrip.Closed += (_, _) => Mic.MenuOpen = false;
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_tray?.ContextMenuStrip is not { } menu)
        {
            return;
        }

        foreach (var old in menu.Items.Cast<Forms.ToolStripItem>().ToList())
        {
            old.Dispose();
        }

        menu.Items.Clear();
        menu.Renderer = MicMenu.Renderer;
        menu.Font = MicMenu.MenuFont;
        menu.Items.Add(MicMenu.Item(MainIsOpen ? "إخفاء نافذة صدى" : "فتح صدى", ToggleMain, bold: true));
        menu.Items.Add(MicMenu.Item(Mic.IsVisible ? "إخفاء زر المايك" : "إظهار زر المايك", () => SetMicVisible(!Mic.IsVisible)));
        menu.Items.Add(MicMenu.Item("تفريغ ملف صوتي…", () => ShowMain("files")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(MicMenu.Item("خروج", ExitApp));
    }

    private Forms.ContextMenuStrip? _micMenu;

    /// <summary>قائمة الزر الأيمن على زر المايك: فتح/إخفاء البرنامج وخيارات الزر.</summary>
    private void ShowMicMenu()
    {
        _micMenu?.Dispose();
        var s = Services.Settings;
        var menu = MicMenu.Create();

        menu.Items.Add(MicMenu.Item(MainIsOpen ? "إخفاء نافذة صدى" : "فتح صدى", ToggleMain, bold: true));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var writing = MicMenu.Item("طريقة الكتابة", null);
        foreach (var (id, label) in new[] { ("normal", "كما قلته"), ("formal", "صياغة رسمية بالفصحى"), ("email", "رسالة إيميل"), ("english", "ترجمة للإنجليزي") })
        {
            writing.DropDownItems.Add(MicMenu.Radio(label, string.Equals(s.WritingMode, id, StringComparison.OrdinalIgnoreCase), () => ChangeMic(() => s.WritingMode = id)));
        }

        menu.Items.Add(writing);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(MicMenu.Check("فوق كل البرامج دائمًا", s.MicAlwaysOnTop, () => ChangeMic(() => s.MicAlwaysOnTop = !s.MicAlwaysOnTop)));

        var size = MicMenu.Item("حجم الزر", null);
        foreach (var (id, label) in new[] { ("small", "صغير"), ("medium", "متوسط"), ("large", "كبير") })
        {
            size.DropDownItems.Add(MicMenu.Radio(label, string.Equals(s.MicSize, id, StringComparison.OrdinalIgnoreCase), () => ChangeMic(() => s.MicSize = id)));
        }

        menu.Items.Add(size);

        var color = MicMenu.Item("لون الزر", null);
        foreach (var (id, label) in new[] { ("gold", "ذهبي"), ("teal", "فيروزي"), ("copper", "نحاسي"), ("blue", "أزرق"), ("purple", "بنفسجي"), ("green", "أخضر"), ("dark", "داكن") })
        {
            var item = MicMenu.Radio(label, string.Equals(s.MicColor, id, StringComparison.OrdinalIgnoreCase), () => ChangeMic(() => s.MicColor = id));
            item.Image = MicMenu.Swatch(id, MicWindow.Themes[id].Top, MicWindow.Themes[id].Bottom);
            color.DropDownItems.Add(item);
        }

        menu.Items.Add(color);
        menu.Items.Add(MicMenu.Check("شفاف قليلًا وهو غير مستخدم", s.MicDimWhenIdle, () => ChangeMic(() => s.MicDimWhenIdle = !s.MicDimWhenIdle)));
        menu.Items.Add(MicMenu.Item("إرجاع الزر لمكانه (أسفل يمين الشاشة)", () => Mic.ResetPosition()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(MicMenu.Item("تفريغ ملف صوتي…", () => ShowMain("files")));
        var hide = MicMenu.Item("إخفاء زر المايك", () => SetMicVisible(false));
        hide.ShortcutKeyDisplayString = "Ctrl+Alt+Shift+M";
        menu.Items.Add(hide);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(MicMenu.Item("خروج", ExitApp));

        // القائمة تحتاج أن تكون في المقدمة حتى تُغلق بالنقر خارجها وتعمل معها Esc والأسهم،
        // وبعد إغلاقها نرجع التركيز للبرنامج الذي كنت تكتب فيه
        var previous = NativeMethods.GetForegroundWindow();
        menu.Closed += (_, _) =>
        {
            Mic.MenuOpen = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (previous == 0 || _main is { IsActive: true })
                {
                    return; // اخترت فتح البرنامج: يبقى في المقدمة
                }

                var foreground = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(foreground, out var owner);
                if (foreground == 0 || owner == (uint)Environment.ProcessId)
                {
                    NativeMethods.SetForegroundWindow(previous);
                }
            });
        };
        Mic.MenuOpen = true;
        _micMenu = menu;
        menu.Show(Forms.Cursor.Position);
        NativeMethods.SetForegroundWindow(menu.Handle);
    }

    private void ChangeMic(Action change)
    {
        change();
        Services.Settings.Save();
        ApplyMicLook();
        _main?.Push("settings.changed", null);
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (!_exiting)
            {
                if (_showEvent.WaitOne(1000))
                {
                    Dispatcher.InvokeAsync(() => ShowMain());
                }
            }
        })
        { IsBackground = true, Name = "VoiceType.ShowListener" };
        thread.Start();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logFile = Path.Combine(AppPaths.Ensure(AppPaths.Logs), "errors.log");
        try
        {
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        MessageBox.Show(
            $"حدث خطأ غير متوقع، والبرنامج سيستمر في العمل.\n\n{e.Exception.Message}\n\nالتفاصيل في:\n{logFile}",
            "صدى", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
