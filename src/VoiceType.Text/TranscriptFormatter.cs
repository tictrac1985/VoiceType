using System.Text;
using VoiceType.Core.Speech;

namespace VoiceType.Text;

/// <summary>
/// يحوّل أجزاء التفريغ إلى نص منسق: فقرات عند التوقفات الطويلة أو عند طول الفقرة، مع ترقيم عربي.
/// لا يغيّر أي كلمة.
/// </summary>
public static class TranscriptFormatter
{
    public static string ToParagraphs(
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan? paragraphPause = null,
        int maxParagraphChars = 450)
    {
        var pause = paragraphPause ?? TimeSpan.FromSeconds(1.5);
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        TimeSpan? lastEnd = null;

        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var longPause = lastEnd is { } end && segment.Start - end >= pause;
            var tooLong = current.Length > maxParagraphChars && EndsSentence(current);
            if (current.Length > 0 && (longPause || tooLong))
            {
                paragraphs.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(text);
            lastEnd = segment.End;
        }

        if (current.Length > 0)
        {
            paragraphs.Add(current.ToString());
        }

        var options = new PunctuationOptions { AddTerminalPunctuation = true, MinWordsForTerminal = 1 };
        return string.Join("\n\n", paragraphs.Select(p => ArabicPunctuation.Normalize(p, options)).Where(p => p.Length > 0));
    }

    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i] is '.' or '؟' or '?' or '!' or '،' or ',';
            }
        }

        return false;
    }
}
