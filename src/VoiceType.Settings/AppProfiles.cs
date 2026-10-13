namespace VoiceType.Settings;

/// <summary>
/// إعدادات خاصة ببرامج معينة: في المحادثات بدون نقطة في آخر الرسالة، في المستندات ترقيم كامل وكل جملة في سطر،
/// في محررات الأكواد بدون إضافات. يُتعرَّف على البرنامج من اسمه أو من عنوان نافذته (لمواقع مثل واتساب ويب).
/// </summary>
public sealed class AppProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = string.Empty;

    /// <summary>أسماء البرامج (مثل WhatsApp أو WINWORD)، أو title:نص يظهر في عنوان النافذة (مثل title:Gmail).</summary>
    public List<string> Match { get; set; } = [];

    /// <summary>chat / document / code / default.</summary>
    public string Style { get; set; } = "default";

    /// <summary>null = حسب الإعداد العام. وإلا: normal / formal / email / english.</summary>
    public string? Writing { get; set; }

    public bool Enabled { get; set; } = true;

    public static List<AppProfile> Defaults() =>
    [
        new()
        {
            Id = "chat", Name = "المحادثات", Style = "chat",
            Match = ["WhatsApp", "WhatsApp.Root", "Telegram", "Messenger", "Discord", "Slack", "ms-teams", "Teams", "Signal", "Viber",
                     "title:WhatsApp", "title:Messenger", "title:Telegram", "title:Discord"],
        },
        new()
        {
            Id = "docs", Name = "المستندات", Style = "document",
            Match = ["WINWORD", "wordpad", "notepad", "ONENOTE", "Obsidian", "Notion", "title:Google Docs", "title:مستندات Google"],
        },
        new()
        {
            Id = "mail", Name = "البريد", Style = "document",
            Match = ["OUTLOOK", "olk", "Thunderbird", "title:Gmail", "title:Outlook"],
        },
        new()
        {
            Id = "code", Name = "الأكواد", Style = "code",
            Match = ["Code", "Code - Insiders", "Cursor", "Windsurf", "devenv", "rider64", "idea64", "pycharm64", "WindowsTerminal", "sublime_text", "notepad++"],
        },
    ];

    /// <summary>
    /// الإعداد المناسب للبرنامج الحالي. عنوان النافذة له الأولوية (المتصفح نفسه قد يفتح واتساب أو Gmail).
    /// </summary>
    public static AppProfile? Find(IEnumerable<AppProfile>? profiles, string? processName, string? windowTitle)
    {
        var list = profiles?.Where(p => p.Enabled).ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            foreach (var profile in list)
            {
                if (profile.Match.Any(m => m.StartsWith("title:", StringComparison.OrdinalIgnoreCase) &&
                                           m.Length > 6 && windowTitle.Contains(m[6..].Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    return profile;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            foreach (var profile in list)
            {
                if (profile.Match.Any(m => string.Equals(m.Trim(), processName, StringComparison.OrdinalIgnoreCase)))
                {
                    return profile;
                }
            }
        }

        return null;
    }
}
