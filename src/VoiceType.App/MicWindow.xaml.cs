using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceType.App.Services;
using static VoiceType.App.Services.NativeMethods;
using Forms = System.Windows.Forms;

namespace VoiceType.App;

/// <summary>
/// زر المايك العائم. لا يأخذ التركيز أبدًا (WS_EX_NOACTIVATE)، فيبقى مؤشر الكتابة في البرنامج الذي تكتب فيه.
/// - يبقى فوق كل البرامج وفوق شريط المهام (اختياري)، ويرجع للأمام تلقائيًا لو غطّاه شريط المهام.
/// - لا يمكن أن يضيع تحت شريط المهام أو خارج الشاشة: عند تركه يُزاح لأقرب مكان ظاهر.
/// - حجمه ولونه وشفافيته من الإعدادات أو من قائمة الزر الأيمن.
/// </summary>
public partial class MicWindow : Window
{
    private const double BaseBox = 64;     // مقاس تصميم الزر في XAML
    private const double BodyInset = 9;    // المسافة بين حافة الصندوق وجسم الزر (64 - 46) / 2

    private readonly DispatcherTimer _bubbleTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _raiseSoon = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WinEventProc? _foregroundProc;
    private nint _foregroundHook;
    private nint _handle;

    private Point _pressOffset;
    private Point _pressScreen;
    private bool _pressed;
    private bool _dragging;
    private bool _hover;
    private double _smoothedLevel;
    private Storyboard? _spinner;

    private DictationState _state = DictationState.Idle;
    private double _scale = 0.8;
    private (string Top, string Bottom, string Glow) _idleColors = Themes["blue"];
    private bool _alwaysOnTop = true;
    private bool _dimWhenIdle;
    private Point _button;          // أعلى يسار صندوق الزر على الشاشة
    private bool _bubbleOnLeft = true;
    private bool _placed;
    private bool _refitting;

    public static readonly IReadOnlyDictionary<string, (string Top, string Bottom, string Glow)> Themes =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["blue"] = ("#3B82F6", "#6366F1", "#4F46E5"),
            ["purple"] = ("#A855F7", "#EC4899", "#C026D3"),
            ["green"] = ("#10B981", "#0EA5E9", "#059669"),
            ["dark"] = ("#4B5563", "#111827", "#000000"),
            ["gold"] = ("#F6CF7E", "#B9822F", "#C98B2E"),
            ["teal"] = ("#7FF3E1", "#14958B", "#14B8A6"),
            ["copper"] = ("#FFB37A", "#D9662A", "#E2622A"),
        };

    public MicWindow()
    {
        InitializeComponent();
        Height = 120;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Unhook();
        MicButton.MouseLeftButtonDown += OnPress;
        MicButton.MouseMove += OnMove;
        MicButton.MouseLeftButtonUp += OnRelease;
        MicButton.LostMouseCapture += (_, _) =>
        {
            // ضاع الإمساك بالماوس (قائمة، قفل الشاشة، Alt+Tab…): ننهي السحب حتى لا يلحق الزر بالماوس
            if (_pressed)
            {
                EndPress(click: false);
            }
        };
        MicButton.MouseEnter += (_, _) => SetHover(true);
        MicButton.MouseLeave += (_, _) => SetHover(false);
        MicButton.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            MenuRequested?.Invoke();
        };
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            Bubble.Visibility = Visibility.Collapsed;
        };
        _topmostTimer.Tick += (_, _) => KeepOnTop();
        _raiseSoon.Tick += (_, _) =>
        {
            _raiseSoon.Stop();
            KeepOnTop();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _topmostTimer.Start();
                KeepOnTop();
            }
            else
            {
                _topmostTimer.Stop();
            }
        };

        // تغيّر الشاشات أو مكان/حجم شريط المهام: نتأكد أن الزر ما زال ظاهرًا
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // ننشئ النافذة مبكرًا حتى نعرف دقة الشاشة (DPI) قبل حساب مكان الزر
        new WindowInteropHelper(this).EnsureHandle();
    }

    public event Action? Clicked;

    public event Action? MenuRequested;

    /// <summary>بعد سحب الزر لمكان جديد (مكان الزر نفسه على الشاشة، لحفظه).</summary>
    public event Action<double, double>? Moved;

    /// <summary>أثناء ظهور قائمة الزر الأيمن لا نرفع الزر فوقها.</summary>
    public bool MenuOpen { get; set; }

    public static double ScaleOf(string? size) => size?.ToLowerInvariant() switch
    {
        "large" => 1.15,
        "medium" => 0.96,
        _ => 0.8,
    };

    private double Box => BaseBox * _scale;

    /// <summary>يطبق الحجم واللون والبقاء في الأعلى والشفافية.</summary>
    public void ApplyLook(string? size, string? color, bool alwaysOnTop, bool dimWhenIdle)
    {
        var center = new Point(_button.X + (Box / 2), _button.Y + (Box / 2));
        _scale = ScaleOf(size);
        SizeScale.ScaleX = _scale;
        SizeScale.ScaleY = _scale;
        _idleColors = color is not null && Themes.TryGetValue(color, out var theme) ? theme : Themes["blue"];
        _alwaysOnTop = alwaysOnTop;
        _dimWhenIdle = dimWhenIdle;
        Topmost = alwaysOnTop;
        ApplyColors();
        UpdateOpacity(animate: false);

        if (_placed)
        {
            // الحجم تغيّر: نحافظ على مركز الزر في نفس المكان
            var before = _button;
            _button = new Point(center.X - (Box / 2), center.Y - (Box / 2));
            KeepInsideScreen();
            Layout(allowFlip: true);
            if (before != _button)
            {
                SavePosition();
            }
        }

        SetTopmost(alwaysOnTop);
    }

    /// <summary>
    /// يضع الزر في مكانه المحفوظ (بالبكسل الحقيقي للشاشة، حتى يصح مع الشاشات مختلفة التكبير)،
    /// أو أسفل يمين الشاشة الرئيسية. يبقى داخل الجزء الظاهر من الشاشة دائمًا.
    /// </summary>
    public void PlaceButton(double? pixelX, double? pixelY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (pixelX is null || pixelY is null)
        {
            var area = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).WorkingArea;
            var boxPx = Box * dpi.DpiScaleX;
            pixelX = area.Right - boxPx - (18 * dpi.DpiScaleX);
            pixelY = area.Bottom - boxPx - (18 * dpi.DpiScaleY);
        }

        _button = new Point(pixelX.Value / dpi.DpiScaleX, pixelY.Value / dpi.DpiScaleY);
        _placed = true;
        KeepInsideScreen();
        Layout(allowFlip: true);
    }

    public void ResetPosition()
    {
        PlaceButton(null, null);
        SavePosition();
    }

    /// <summary>مكان الزر بالبكسل الحقيقي (للحفظ).</summary>
    private void SavePosition()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Moved?.Invoke(_button.X * dpi.DpiScaleX, _button.Y * dpi.DpiScaleY);
    }

    /// <summary>
    /// انتقل الزر لشاشة بتكبير مختلف: نحوّل مكانه للوحدات الجديدة ونعيد ترتيب النافذة،
    /// وإلا يقفز الزر أو يخرج من الشاشة.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        var fx = oldDpi.DpiScaleX / newDpi.DpiScaleX;
        var fy = oldDpi.DpiScaleY / newDpi.DpiScaleY;
        _button = new Point(_button.X * fx, _button.Y * fy);
        if (_pressed)
        {
            _pressOffset = new Point(_pressOffset.X * fx, _pressOffset.Y * fy);
            _pressScreen = new Point(_pressScreen.X * fx, _pressScreen.Y * fy);
        }
        else
        {
            Dispatcher.InvokeAsync(Refit);
        }
    }

    public void SetState(DictationState state)
    {
        _state = state;
        _spinner?.Stop();
        Spinner.Visibility = Visibility.Collapsed;
        StopIcon.Visibility = Visibility.Collapsed;
        MicIcon.Visibility = Visibility.Visible;
        LevelRing.Opacity = 0;

        switch (state)
        {
            case DictationState.Recording:
                MicIcon.Visibility = Visibility.Collapsed;
                StopIcon.Visibility = Visibility.Visible;
                LevelRing.Opacity = 1;
                ShowBubble("أتكلم… انقر للإيقاف", seconds: 2.5);
                break;
            case DictationState.Processing:
                MicIcon.Visibility = Visibility.Collapsed;
                Spinner.Visibility = Visibility.Visible;
                StartSpinner();
                break;
        }

        ApplyColors();
        UpdateOpacity(animate: true);
    }

    public void SetLevel(double level)
    {
        _smoothedLevel = (_smoothedLevel * 0.6) + (Math.Min(1, level * 2.2) * 0.4);
        var scale = 1 + (_smoothedLevel * 0.5);
        LevelScale.ScaleX = scale;
        LevelScale.ScaleY = scale;
    }

    /// <summary>الكلام أثناء قوله (آخر جزء منه فقط حتى تبقى الفقاعة صغيرة).</summary>
    public void ShowLive(string text)
    {
        text = text.Trim();
        if (text.Length > 70)
        {
            var cut = text.IndexOf(' ', text.Length - 70);
            text = "…" + text[(cut > 0 ? cut + 1 : text.Length - 70)..];
        }

        BubbleText.Text = text;
        Bubble.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x11, 0x18, 0x27));
        Bubble.Visibility = Visibility.Visible;
        _bubbleTimer.Interval = TimeSpan.FromSeconds(2.5);
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    public void ShowBubble(string text, bool error = false, double seconds = 3)
    {
        BubbleText.Text = text;
        seconds = Math.Max(seconds, text.Length / 12.0);
        Bubble.Background = new SolidColorBrush(error ? Color.FromArgb(0xEE, 0x99, 0x1B, 0x1B) : Color.FromArgb(0xEE, 0x11, 0x18, 0x27));
        Bubble.Visibility = Visibility.Visible;
        _bubbleTimer.Interval = TimeSpan.FromSeconds(seconds);
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    // ===== الشكل =====
    private void ApplyColors()
    {
        var (top, bottom, glow) = _state switch
        {
            DictationState.Recording => ("#F87171", "#DC2626", "#DC2626"),
            DictationState.Processing => ("#FBBF24", "#F59E0B", "#D97706"),
            _ => _idleColors,
        };
        BodyTop.Color = Parse(top);
        BodyBottom.Color = Parse(bottom);
        Glow.Color = Parse(glow);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private void SetHover(bool hover)
    {
        _hover = hover;
        var to = hover ? 1.08 : 1.0;
        var duration = TimeSpan.FromMilliseconds(140);
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration));
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration));
        UpdateOpacity(animate: true);
    }

    private void UpdateOpacity(bool animate)
    {
        var target = _dimWhenIdle && _state == DictationState.Idle && !_hover && !_dragging ? 0.5 : 1.0;
        if (animate)
        {
            MicButton.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            MicButton.BeginAnimation(OpacityProperty, null);
            MicButton.Opacity = target;
        }
    }

    private void StartSpinner()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(animation, Spinner);
        Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.Angle"));
        _spinner = new Storyboard();
        _spinner.Children.Add(animation);
        _spinner.Begin();
    }

    // ===== المكان =====

    /// <summary>يرتب النافذة حول الزر: الفقاعة تظهر في الجهة التي فيها مساحة.</summary>
    private void Layout(bool allowFlip)
    {
        var box = Box;
        if (allowFlip)
        {
            var area = WorkAreaAt(new Point(_button.X + (box / 2), _button.Y + (box / 2)));
            _bubbleOnLeft = _button.X + (box / 2) > area.Left + (area.Width / 2);
        }

        var side = _bubbleOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        MicButton.HorizontalAlignment = side;
        Bubble.HorizontalAlignment = side;
        Bubble.Margin = _bubbleOnLeft ? new Thickness(0, 0, box + 2, 0) : new Thickness(box + 2, 0, 0, 0);
        Left = _bubbleOnLeft ? _button.X - (Width - box) : _button.X;
        Top = _button.Y - ((Height - box) / 2);
    }

    /// <summary>الزر يبقى كاملًا داخل الجزء الظاهر من الشاشة (فوق شريط المهام وليس تحته).</summary>
    private void KeepInsideScreen()
    {
        var box = Box;
        var inset = BodyInset * _scale;
        var area = WorkAreaAt(new Point(_button.X + (box / 2), _button.Y + (box / 2)));
        var x = Math.Clamp(_button.X, area.Left - inset, Math.Max(area.Left - inset, area.Right - box + inset));
        var y = Math.Clamp(_button.Y, area.Top - inset, Math.Max(area.Top - inset, area.Bottom - box + inset));
        _button = new Point(x, y);
    }

    /// <summary>المساحة الظاهرة (بدون شريط المهام) للشاشة التي فيها النقطة، بوحدات WPF.</summary>
    private Rect WorkAreaAt(Point dip)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var device = new System.Drawing.Point((int)(dip.X * dpi.DpiScaleX), (int)(dip.Y * dpi.DpiScaleY));
            var area = Forms.Screen.FromPoint(device).WorkingArea;
            return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SystemParameters.WorkArea;
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.InvokeAsync(Refit);
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Refit);

    private void Refit()
    {
        if (!_placed || _pressed || _refitting)
        {
            return;
        }

        _refitting = true;
        try
        {
            KeepInsideScreen();
            Layout(allowFlip: true);
        }
        finally
        {
            _refitting = false;
        }
    }

    // ===== فوق كل البرامج =====
    private void SetTopmost(bool on)
    {
        if (_handle == 0)
        {
            return;
        }

        SetWindowPos(_handle, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>شريط المهام وبعض البرامج تأخذ مكان «الأعلى» عند النقر عليها، فنرجع الزر للأمام.</summary>
    private void KeepOnTop()
    {
        if (_alwaysOnTop && IsVisible && !MenuOpen && !_pressed)
        {
            SetTopmost(true);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GWL_EXSTYLE);
        SetWindowLongPtr(_handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        SetTopmost(_alwaysOnTop);

        // عند تغيّر البرنامج الأمامي (مثل النقر على شريط المهام) نرفع الزر من جديد
        _foregroundProc = (_, _, _, _, _, _, _) => Dispatcher.InvokeAsync(() =>
        {
            KeepOnTop();
            _raiseSoon.Stop();
            _raiseSoon.Start();
        });
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void Unhook()
    {
        _topmostTimer.Stop();
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE; // النقر لا ينقل التركيز من البرنامج الحالي
        }

        return 0;
    }

    // ===== السحب والنقر =====
    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _pressScreen = ToScreenDip(e);
        _pressOffset = new Point(_pressScreen.X - _button.X, _pressScreen.Y - _button.Y);
        MicButton.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPress(click: false);
            return;
        }

        var current = ToScreenDip(e);
        if (!_dragging && (Math.Abs(current.X - _pressScreen.X) > 5 || Math.Abs(current.Y - _pressScreen.Y) > 5))
        {
            _dragging = true;
            UpdateOpacity(animate: true);
        }

        if (_dragging)
        {
            _button = new Point(current.X - _pressOffset.X, current.Y - _pressOffset.Y);
            Layout(allowFlip: false);
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        e.Handled = true;
        EndPress(click: true);
    }

    /// <summary>نهاية الضغط: إما نقرة، أو نهاية سحب (نثبت الزر داخل الشاشة ونحفظ مكانه).</summary>
    private void EndPress(bool click)
    {
        var dragged = _dragging;
        _pressed = false;
        _dragging = false;
        if (MicButton.IsMouseCaptured)
        {
            MicButton.ReleaseMouseCapture();
        }

        if (dragged)
        {
            KeepInsideScreen();
            Layout(allowFlip: true);
            UpdateOpacity(animate: true);
            KeepOnTop();
            SavePosition();
        }
        else if (click)
        {
            Clicked?.Invoke();
        }
    }

    private Point ToScreenDip(MouseEventArgs e)
    {
        var device = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target ? target.TransformFromDevice.Transform(device) : device;
    }
}
