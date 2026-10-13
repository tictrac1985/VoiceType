using System.Text;

namespace VoiceType.Text;

/// <summary>
/// ضبط اتجاه النص العربي المختلط بالإنجليزي عند لصقه في البرامج الأخرى.
///
/// المشكلة: كثير من البرامج تحدد اتجاه السطر من أول حرف فيه، أو تجعل الاتجاه من اليسار لليمين دائمًا،
/// فإذا كان في السطر كلمات إنجليزية تتلخبط الجملة، وتظهر النقطة وعلامة الاستفهام في الجهة الخطأ.
///
/// الحل: لكل سطر فيه عربي نضيف علامات اتجاه غير مرئية (لا تظهر ولا تُطبع):
/// - RLM في بداية السطر ونهايته: يجعل البرامج التي تكتشف الاتجاه تلقائيًا تعتبر السطر عربيًا (من اليمين لليسار)،
///   ويثبّت علامات الترقيم في نهاية الجملة.
/// - RLE … PDF حول السطر المختلط: يجعل ترتيب الكلام صحيحًا حتى في البرامج التي تفرض الاتجاه من اليسار لليمين.
/// الأسطر الإنجليزية بالكامل تبقى كما هي.
/// </summary>
public static class BidiFormatter
{
    public const char Rlm = '‏';
    public const char Rle = '‫';
    public const char Pdf = '‬';

    private static readonly char[] Marks = ['‎', '‏', '‪', '‫', '‬', '‭', '‮', '⁦', '⁧', '⁨', '⁩'];

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text) || !ArabicText.ContainsArabic(text))
        {
            return text;
        }

        var lines = Strip(text).Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(text.Length + (lines.Length * 4));
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(FormatLine(lines[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// هل يبدأ النص بالعربي؟ (أول حرف «قوي» عربي، بعد تجاهل الأرقام والرموز وعلامات الاتجاه).
    /// يُستخدم لجعل الفقرة من اليمين لليسار في Word وغيره عند الإملاء بالعربي.
    /// </summary>
    public static bool StartsRightToLeft(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (ArabicText.IsArabicLetter(c))
            {
                return true;
            }

            if (char.IsLetter(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>يحذف علامات الاتجاه (مثلًا قبل إعادة التنسيق أو عند النسخ لمحرر أكواد).</summary>
    public static string Strip(string text) =>
        text.IndexOfAny(Marks) < 0 ? text : new string(text.Where(c => Array.IndexOf(Marks, c) < 0).ToArray());

    private static string FormatLine(string line)
    {
        if (line.Trim().Length == 0 || !ArabicText.ContainsArabic(line))
        {
            return line;
        }

        var mixed = line.Any(ArabicText.IsLatinLetter) || line.Any(char.IsDigit);
        return mixed
            ? $"{Rlm}{Rle}{line}{Pdf}{Rlm}"
            : $"{Rlm}{line}{Rlm}";
    }
}
