using System.Text;
using System.Text.RegularExpressions;

namespace VoiceType.Text;

/// <summary>متى يبدأ النص سطرًا جديدًا.</summary>
public enum LineBreakMode
{
    /// <summary>فقرة واحدة، إلا إذا قال المستخدم "سطر جديد".</summary>
    None,

    /// <summary>كل جملة كاملة (تنتهي بـ . أو ؟ أو !) في سطر.</summary>
    Sentences,
}

/// <summary>
/// الأسطر الجديدة في النص المُملى:
/// أوامر صوتية ("سطر جديد"، "فقرة جديدة"، "new line") + خيار جعل كل جملة في سطر.
/// </summary>
public static partial class LineBreaks
{
    // الأمر مع أي ترقيم أو مسافات حوله، لأن النموذج قد يكتب "، سطر جديد."
    [GeneratedRegex(@"[\s،,؛;:]*(?<!\w)(?:(?<para>فقرة جديدة|فقره جديده|فقرة جديده|new paragraph)|(?<line>سطر جديد|سطر جديدة|سطر جديده|new line|newline))(?!\w)[\s،,.؛;:!]*", RegexOptions.IgnoreCase)]
    private static partial Regex CommandRegex();

    // نهاية جملة متبوعة بمسافة ثم كلام (وليس رقمًا عشريًا أو رابطًا لأنهما بلا مسافة)
    [GeneratedRegex(@"(?<=[.!؟?…])[ \t]+(?=\S)")]
    private static partial Regex SentenceEndRegex();

    /// <summary>يقسم النص عند الأوامر الصوتية. يرجع أجزاء الكلام والفواصل بينها ("\n" أو "\n\n").</summary>
    public static IReadOnlyList<(string Text, string Separator)> SplitCommands(string text)
    {
        var parts = new List<(string, string)>();
        var last = 0;
        foreach (Match match in CommandRegex().Matches(text))
        {
            parts.Add((text[last..match.Index], match.Groups["para"].Success ? "\n\n" : "\n"));
            last = match.Index + match.Length;
        }

        parts.Add((text[last..], string.Empty));
        return parts;
    }

    public static bool ContainsCommand(string text) => CommandRegex().IsMatch(text);

    /// <summary>كل جملة في سطر.</summary>
    public static string BreakSentences(string text) => SentenceEndRegex().Replace(text, "\n");

    /// <summary>يجمع الأجزاء بعد معالجة كل جزء، ويزيل الأسطر الفارغة الزائدة.</summary>
    public static string Join(IEnumerable<(string Text, string Separator)> parts)
    {
        var builder = new StringBuilder();
        foreach (var (part, separator) in parts)
        {
            builder.Append(part.Trim());
            builder.Append(separator);
        }

        var result = Regex.Replace(builder.ToString(), @"\n{3,}", "\n\n");
        return result.TrimStart('\n');
    }

    /// <summary>ويندوز وبرامجه (مثل Word) تتوقع CRLF عند اللصق.</summary>
    public static string ToWindows(string text) => Regex.Replace(text.Replace("\r\n", "\n"), "\n", "\r\n");
}
