using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VoiceType.App.Services;

/// <summary>
/// يعرف لوحده إن اجتماع بدأ: ويندوز بيسجّل مين بيستخدم الميكروفون دلوقتي
/// (HKCU\...\CapabilityAccessManager\ConsentStore\microphone: LastUsedTimeStop = 0 معناها شغال دلوقتي).
/// لو Zoom أو Teams أو Webex أو Skype أو Slack أو Discord، أو متصفح فاتح Google Meet / Zoom / Teams، بيستخدم الميكروفون
/// ← صدى يقترح يسجّل الاجتماع. ولما الاجتماع يخلص وصدى بيسجّل ← يقترح يوقف ويفرّغ.
/// بيقرا بس (مفيش أي تعديل في الويندوز)، وكل 5 ثواني.
/// </summary>
internal sealed class MeetingDetector : IDisposable
{
    private const string MicStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>برامج الاجتماعات (اسم الملف) والاسم اللي يظهر للمستخدم.</summary>
    private static readonly Dictionary<string, string> MeetingApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Teams",
        ["teams.exe"] = "Teams",
        ["msteams.exe"] = "Teams",
        ["webex.exe"] = "Webex",
        ["ciscocollabhost.exe"] = "Webex",
        ["atmgr.exe"] = "Webex",
        ["skype.exe"] = "Skype",
        ["slack.exe"] = "Slack",
        ["discord.exe"] = "Discord",
        ["gotomeeting.exe"] = "GoTo Meeting",
    };

    /// <summary>البرامج من المتجر (اسم الحزمة يبدأ بـ).</summary>
    private static readonly (string Prefix, string Name)[] PackagedApps =
    [
        ("MSTeams_", "Teams"),
        ("MicrosoftTeams_", "Teams"),
        ("Microsoft.SkypeApp_", "Skype"),
        ("5319275A.WhatsAppDesktop_", "WhatsApp"),
        ("ZoomVideoCommunications", "Zoom"),
    ];

    private static readonly string[] Browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"];

    /// <summary>كلمات في عنوان نافذة المتصفح تدل على اجتماع.</summary>
    private static readonly (string Text, string Name)[] BrowserMeetings =
    [
        ("Meet - ", "Google Meet"),
        ("Meet – ", "Google Meet"),
        ("Google Meet", "Google Meet"),
        ("Zoom Meeting", "Zoom"),
        ("Zoom Workplace", "Zoom"),
        ("Microsoft Teams", "Teams"),
        ("Webex", "Webex"),
        ("Jitsi Meet", "Jitsi"),
    ];

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private string? _current;

    public MeetingDetector()
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>اجتماع بدأ (اسم البرنامج).</summary>
    public event Action<string>? Started;

    /// <summary>الاجتماع اللي كان شغال خلص (البرنامج ساب الميكروفون).</summary>
    public event Action<string>? Ended;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _current = null;
            }
        }
    }

    public void Dispose() => _timer.Stop();

    private void Check()
    {
        string? now;
        try
        {
            now = ActiveMeeting();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return;
        }

        if (now is not null && _current is null)
        {
            _current = now;
            Started?.Invoke(now);
        }
        else if (now is null && _current is not null)
        {
            var ended = _current;
            _current = null;
            Ended?.Invoke(ended);
        }
    }

    /// <summary>اسم برنامج الاجتماع اللي بيستخدم الميكروفون دلوقتي، أو null.</summary>
    internal static string? ActiveMeeting()
    {
        using var store = Registry.CurrentUser.OpenSubKey(MicStore);
        if (store is null)
        {
            return null;
        }

        var browserUsesMic = false;
        using (var nonPackaged = store.OpenSubKey("NonPackaged"))
        {
            foreach (var name in nonPackaged?.GetSubKeyNames() ?? [])
            {
                using var app = nonPackaged!.OpenSubKey(name);
                if (!InUse(app))
                {
                    continue;
                }

                var exe = name[(name.LastIndexOf('#') + 1)..];
                if (MeetingApps.TryGetValue(exe, out var display))
                {
                    return display;
                }

                if (Browsers.Any(b => string.Equals(Path.GetFileNameWithoutExtension(exe), b, StringComparison.OrdinalIgnoreCase)))
                {
                    browserUsesMic = true;
                }
            }
        }

        foreach (var name in store.GetSubKeyNames())
        {
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = PackagedApps.FirstOrDefault(p => name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                continue;
            }

            using var app = store.OpenSubKey(name);
            if (InUse(app))
            {
                return match.Name;
            }
        }

        return browserUsesMic ? BrowserMeeting() : null;
    }

    /// <summary>المتصفح بيستخدم الميكروفون: نتأكد من عنوان النافذة إنه اجتماع (مش موقع تاني).</summary>
    private static string? BrowserMeeting()
    {
        foreach (var browser in Browsers)
        {
            foreach (var process in Process.GetProcessesByName(browser))
            {
                using (process)
                {
                    string title;
                    try
                    {
                        title = process.MainWindowTitle;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    var match = BrowserMeetings.FirstOrDefault(m => title.Contains(m.Text, StringComparison.OrdinalIgnoreCase));
                    if (match.Name is not null)
                    {
                        return match.Name;
                    }
                }
            }
        }

        return null;
    }

    private static bool InUse(RegistryKey? app) =>
        app?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && app.GetValue("LastUsedTimeStart") is long start && start > 0;
}
