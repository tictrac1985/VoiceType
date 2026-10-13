using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using static VoiceType.App.Services.NativeMethods;

namespace VoiceType.App.Services;

/// <summary>
/// يكتب النص في البرنامج الذي عليه مؤشر الكتابة: يضعه في الحافظة مؤقتًا ثم Ctrl+V، ثم يعيد محتوى الحافظة الأصلي.
/// النص المؤقت لا يُحفظ في سجل حافظة Windows (Win+V) ولا يُزامن سحابيًا.
/// يجب استدعاؤه من خيط الواجهة (STA).
/// </summary>
public static class TextInjector
{
    /// <summary>إضافة علامات الاتجاه غير المرئية للعربي المختلط بالإنجليزي (من الإعدادات).</summary>
    public static bool FixMixedDirection { get; set; } = true;

    /// <summary>عند الإملاء بالعربي: نجعل الفقرة من اليمين لليسار قبل اللصق (من الإعدادات).</summary>
    public static bool ArabicStartsRight { get; set; } = true;

    /// <summary>
    /// البرامج التي تفهم اختصار Windows القياسي «Ctrl + Shift الأيمن = فقرة من اليمين».
    /// قائمة محددة عمدًا: في البرامج الأخرى قد يكون للاختصار معنى مختلف.
    /// </summary>
    private static readonly HashSet<string> RtlShortcutApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "wordpad", "notepad", "OUTLOOK", "ONENOTE", "POWERPNT",
        "chrome", "msedge", "brave", "opera", "vivaldi", "firefox", "Telegram",
    };

    // محررات الأكواد والطرفيات: لا نضيف فيها أي علامات مخفية حتى لا تفسد الأكواد
    private static readonly HashSet<string> CodeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "Cursor", "Windsurf", "Trae", "devenv", "rider64", "idea64", "pycharm64", "webstorm64",
        "clion64", "goland64", "phpstorm64", "studio64", "sublime_text", "notepad++", "WindowsTerminal", "cmd", "powershell",
        "pwsh", "conhost", "OpenConsole", "wt", "mintty", "Zed", "nvim-qt", "gvim",
    };

    /// <summary>اسم البرنامج الذي عليه التركيز الآن (مثل WINWORD أو chrome).</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
            {
                return null;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>النص كما سيُلصق في البرنامج الحالي: مع علامات الاتجاه، إلا في محررات الأكواد.</summary>
    private static string PrepareForTarget(string text, string? processName) =>
        FixMixedDirection && (processName is null || !CodeApps.Contains(processName))
            ? VoiceType.Text.BidiFormatter.Apply(text)
            : VoiceType.Text.BidiFormatter.Strip(text);
    /// <summary>ما كُتب (لأوامر «امسح آخر جملة» و«امسح الكل»): رقم الإملاء، والنافذة، والوقت.</summary>
    public sealed record Insertion(int Dictation, nint Window, DateTime Time);

    private static readonly List<Insertion> InsertionsList = [];

    public static IReadOnlyList<Insertion> Insertions => InsertionsList;

    public static void ForgetInsertion(Insertion insertion) => InsertionsList.Remove(insertion);

    /// <summary>عنوان النافذة الحالية (لمعرفة واتساب ويب أو Gmail داخل المتصفح).</summary>
    public static string? ForegroundTitle()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        var buffer = new char[512];
        var length = GetWindowText(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    public static nint ForegroundWindow() => GetForegroundWindow();

    /// <summary>تراجع (Ctrl+Z) عدد مرات: كل لصق في البرامج خطوة تراجع واحدة.</summary>
    public static async Task UndoAsync(int times)
    {
        await WaitForModifiersReleasedAsync();
        for (var i = 0; i < times; i++)
        {
            SendCombo(VK_CONTROL, VK_Z);
            await Task.Delay(120);
        }
    }

    /// <summary>يمسح آخر كلمة (Ctrl+Backspace).</summary>
    public static async Task DeleteWordAsync()
    {
        await WaitForModifiersReleasedAsync();
        SendCombo(VK_CONTROL, VK_BACK);
        await Task.Delay(80);
    }

    private static void SendCombo(ushort modifier, ushort key)
    {
        var inputs = new[] { Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static async Task<bool> InsertAsync(string text, int dictation = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        await WaitForModifiersReleasedAsync();

        var process = ForegroundProcessName();
        var startRight = ArabicStartsRight && process is not null && RtlShortcutApps.Contains(process)
                         && VoiceType.Text.BidiFormatter.StartsRightToLeft(text) && !CtrlShiftSwitchesLanguage();
        text = PrepareForTarget(text, process);
        if (startRight)
        {
            // نفس ما يفعله المستخدم بالضغط على Ctrl + Shift الأيمن: الفقرة الحالية تصبح من اليمين لليسار
            SendRightToLeftShortcut();
            await Task.Delay(60);
        }

        // في الكتابة المباشرة: الحافظة الأصلية تُحفظ مرة واحدة وتُعاد في النهاية
        // (وتُحفظ من جديد إذا نسخ المستخدم شيئًا أثناء الكلام)
        DataObject? backup = null;
        if (!_batch)
        {
            backup = CurrentUserClipboard();
        }
        else if (_batchSequence == 0 || GetClipboardSequenceNumber() != _batchSequence)
        {
            _batchBackup = CurrentUserClipboard();
        }

        if (!TrySetClipboard(CreateDataObject(text)))
        {
            return false;
        }

        var ourClipboard = GetClipboardSequenceNumber();
        if (_batch)
        {
            _batchSequence = ourClipboard;
        }
        await Task.Delay(40);
        SendCtrlV();

        if (backup is not null)
        {
            _pendingBackup = backup;
            _pendingSequence = ourClipboard;
            _ = RestoreLaterAsync(backup, ourClipboard);
        }

        InsertionsList.Add(new Insertion(dictation, GetForegroundWindow(), DateTime.UtcNow));
        if (InsertionsList.Count > 60)
        {
            InsertionsList.RemoveAt(0);
        }

        if (_batch)
        {
            // نعطي البرنامج وقتًا ليقرأ الحافظة قبل أن نضع الجملة التالية فيها
            await Task.Delay(250);
        }

        return true;
    }

    /// <summary>
    /// يعيد محتوى الحافظة الأصلي بعد أن يقرأ البرنامج المستهدف النص (البرامج الثقيلة مثل Word تحتاج وقتًا).
    /// يعمل في الخلفية حتى لا يتأخر الإملاء، ولا يعيده إذا نسخ المستخدم شيئًا جديدًا في هذه الأثناء.
    /// </summary>
    private static async Task RestoreLaterAsync(DataObject backup, uint ourClipboard)
    {
        try
        {
            await Task.Delay(800);
            if (GetClipboardSequenceNumber() == ourClipboard && !TrySetClipboard(backup))
            {
                TrySetClipboard(TextOnly(backup));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // إعادة الحافظة ليست ضرورية لنجاح الإملاء
        }
    }

    private static DataObject? _pendingBackup;
    private static uint _pendingSequence;

    /// <summary>
    /// محتوى حافظة المستخدم الحقيقي: إذا كان فيها نصنا الذي لم يُستبدل بعد بالأصل (لصق قبل أقل من ثانية)،
    /// نستخدم النسخة الأصلية المحفوظة بدلًا منه.
    /// </summary>
    private static DataObject? CurrentUserClipboard() =>
        _pendingBackup is not null && GetClipboardSequenceNumber() == _pendingSequence ? _pendingBackup : BackupClipboard();

    private static bool _batch;
    private static DataObject? _batchBackup;
    private static uint _batchSequence;

    /// <summary>بداية كتابة على دفعات (الكتابة المباشرة أثناء الكلام).</summary>
    public static void BeginBatch()
    {
        if (_batch)
        {
            return;
        }

        _batchBackup = null; // تُحفظ عند أول كتابة فعلًا
        _batchSequence = 0;
        _batch = true;
    }

    /// <summary>نهاية الدفعات: يعيد محتوى الحافظة الأصلي إن لم ينسخ المستخدم شيئًا جديدًا.</summary>
    public static void EndBatch()
    {
        if (!_batch)
        {
            return;
        }

        _batch = false;
        var backup = _batchBackup;
        _batchBackup = null;
        if (backup is not null && _batchSequence != 0)
        {
            _pendingBackup = backup;
            _pendingSequence = _batchSequence;
            _ = RestoreLaterAsync(backup, _batchSequence);
        }
    }

    public static void CopyToClipboard(string text) =>
        TrySetClipboard(CreateDataObject(PrepareForTarget(text, processName: null), privateCopy: false));

    private static DataObject CreateDataObject(string text, bool privateCopy = true)
    {
        var data = new DataObject();
        text = VoiceType.Text.LineBreaks.ToWindows(text);
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(DataFormats.Text, text);
        if (privateCopy)
        {
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }

        return data;
    }

    private static DataObject? BackupClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null)
            {
                return null;
            }

            var copy = new DataObject();
            var any = false;
            foreach (var format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    var value = current.GetData(format, autoConvert: false);
                    if (value is not null)
                    {
                        copy.SetData(format, value);
                        any = true;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // بعض الصيغ لا يمكن نسخها، نتجاهلها
                }
            }

            return any ? copy : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>إذا تعذّرت إعادة كل صيغ الحافظة الأصلية، نعيد النص على الأقل.</summary>
    private static DataObject TextOnly(DataObject backup)
    {
        var data = new DataObject();
        if (backup.GetData(DataFormats.UnicodeText) is string text)
        {
            data.SetData(DataFormats.UnicodeText, text);
        }

        return data;
    }

    private static bool TrySetClipboard(IDataObject data)
    {
        // WPF يعيد المحاولة داخليًا، فمحاولتان تكفيان بدون تجميد الواجهة طويلًا
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                Thread.Sleep(30); // برنامج آخر يستخدم الحافظة الآن
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false; // صيغة لا يمكن إعادتها: نتركها
            }
        }

        return false;
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        var keys = new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN, VK_LBUTTON };
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
        {
            await Task.Delay(30);
        }
    }

    /// <summary>
    /// لو كان Ctrl+Shift مستخدمًا لتبديل لغة الكيبورد، لا نرسله (حتى لا تتغير اللغة).
    /// القيمة 2 في إعدادات Windows تعني Ctrl+Shift.
    /// </summary>
    private static bool CtrlShiftSwitchesLanguage()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
            if (key is null)
            {
                return false;
            }

            return new[] { "Hotkey", "Language Hotkey", "Layout Hotkey" }
                .Any(name => string.Equals(key.GetValue(name)?.ToString(), "2", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void SendRightToLeftShortcut()
    {
        var inputs = new[]
        {
            Key(VK_RCONTROL, false, extended: true, scan: 0x1D),
            Key(VK_RSHIFT, false, scan: 0x36),
            Key(VK_RSHIFT, true, scan: 0x36),
            Key(VK_RCONTROL, true, extended: true, scan: 0x1D),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up, bool extended = false, ushort scan = 0) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };
}
