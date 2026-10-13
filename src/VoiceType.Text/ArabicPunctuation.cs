using System.Text;
using System.Text.RegularExpressions;

namespace VoiceType.Text;

public sealed record PunctuationOptions
{
    /// <summary>إضافة نقطة (أو علامة استفهام) في نهاية النص إذا لم تكن موجودة.</summary>
    public bool AddTerminalPunctuation { get; init; } = true;

    /// <summary>أقل عدد كلمات لإضافة علامة النهاية (لا نضيفها لكلمة أو كلمتين).</summary>
    public int MinWordsForTerminal { get; init; } = 3;
}

/// <summary>
/// المستوى الأول من الترقيم: قواعد آمنة لا تغيّر أي كلمة.
/// تحويل , ? ; إلى ، ؟ ؛ في السياق العربي، وضبط المسافات، وعلامة النهاية.
/// لا تلمس الأرقام (1,000 و 3.5) ولا الروابط والإيميلات.
/// </summary>
public static partial class ArabicPunctuation
{
    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "هل", "ليه", "ليش", "لماذا", "إزاي", "ازاي", "كيف", "امتى", "إمتى", "متى",
        "فين", "أين", "وين", "مين", "ماذا", "كام", "كم", "إيه", "ايه",
    };

    [GeneratedRegex(@"\S*(?:://|@|www\.)\S*", RegexOptions.IgnoreCase)]
    private static partial Regex ProtectedTokenRegex();

    [GeneratedRegex(@"(?<=[ء-ي٠-٩])[ \t]+(?=[،؛؟!:.])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"([،؛؟])(?=[^\s\p{P}])")]
    private static partial Regex MissingSpaceAfterArabicPunctuationRegex();

    [GeneratedRegex(@"(?<=[ء-ي])([.!:])(?=[ء-ي])")]
    private static partial Regex MissingSpaceAfterLatinPunctuationRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpacesRegex();

    public static string Normalize(string text, PunctuationOptions? options = null)
    {
        options ??= new PunctuationOptions();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = ConvertToArabicMarks(text.Trim());
        result = SpaceBeforePunctuationRegex().Replace(result, string.Empty);
        result = MissingSpaceAfterArabicPunctuationRegex().Replace(result, "$1 ");
        result = MissingSpaceAfterLatinPunctuationRegex().Replace(result, "$1 ");
        result = MultipleSpacesRegex().Replace(result, " ").Trim();

        if (options.AddTerminalPunctuation)
        {
            result = AddTerminal(result, options.MinWordsForTerminal);
        }

        return result;
    }

    private static string ConvertToArabicMarks(string text)
    {
        var isProtected = new bool[text.Length];
        foreach (Match match in ProtectedTokenRegex().Matches(text))
        {
            Array.Fill(isProtected, true, match.Index, match.Length);
        }

        var arabicDominant = CountArabicLetters(text) >= CountLatinLetters(text);
        var builder = new StringBuilder(text);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not (',' or '?' or ';') || isProtected[i])
            {
                continue;
            }

            var immediatePrev = i > 0 ? text[i - 1] : '\0';
            var immediateNext = i + 1 < text.Length ? text[i + 1] : '\0';
            if (char.IsDigit(immediatePrev) && char.IsDigit(immediateNext))
            {
                continue;
            }

            var prev = ArabicText.PreviousNonSpace(text, i);
            var next = ArabicText.NextNonSpace(text, i);
            var arabicNeighbor = (prev is { } p && ArabicText.IsArabicLetter(p)) ||
                                 (next is { } n && ArabicText.IsArabicLetter(n));
            var latinInArabicSentence = arabicDominant && prev is { } lp && ArabicText.IsLatinLetter(lp);

            if (arabicNeighbor || latinInArabicSentence)
            {
                builder[i] = c switch
                {
                    ',' => '،',
                    '?' => '؟',
                    _ => '؛',
                };
            }
        }

        return builder.ToString();
    }

    private static string AddTerminal(string text, int minWords)
    {
        var last = text[^1];
        if (char.IsPunctuation(last) || char.IsSymbol(last) || !ArabicText.ContainsArabic(text))
        {
            return text;
        }

        var lastSentenceStart = text.LastIndexOfAny(['.', '!', '؟', '?', '\n']) + 1;
        var lastSentence = text[lastSentenceStart..].Trim();
        var words = lastSentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > 0 && QuestionWords.Contains(StripWaw(words[0])))
        {
            return text + "؟";
        }

        var totalWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return totalWords >= minWords ? text + "." : text;
    }

    /// <summary>"وهل" تُعامل مثل "هل".</summary>
    private static string StripWaw(string word) =>
        word.Length > 2 && word[0] == 'و' && QuestionWords.Contains(word[1..]) ? word[1..] : word;

    private static int CountArabicLetters(string text) => text.Count(ArabicText.IsArabicLetter);

    private static int CountLatinLetters(string text) => text.Count(ArabicText.IsLatinLetter);
}
