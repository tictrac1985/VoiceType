using System.Text.RegularExpressions;
using VoiceType.Core.Speech;

namespace VoiceType.Text;

/// <summary>
/// جمل بيؤلفها Whisper (وساعات غيره) عند السكوت أو آخر التسجيل، لأنه اتعلم من ترجمات يوتيوب.
/// أشهرها «ترجمة نانسي قنقر»: بتيجي في آخر الكلام أو لوحدها، وساعات بين قوسين.
/// </summary>
public static class Hallucinations
{
    // جمل مميزة محدش بيقولها في كلامه: بتتشال من أي مكان في النص
    private static readonly Regex Signatures = new(
        @"[\(\[«""]?[ \t]*(?:ترجم[ةه][ \t]*(?:و[ \t]*تدقيق[ \t]*)?[:：\-–]?[ \t]*)?نانسي[ \t]+قنقر[ \t]*[\.!،,؟?]*[ \t]*[\)\]»""]?[ \t]*[\.!،,]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // جمل عادية ممكن تتقال، فبنشيلها بس لو هي الكلام كله
    private static readonly string[] WholeOnly =
    [
        "ترجمة نانسي قنقر", "اشتركوا في القناة", "اشتركوا في القناة ليصلكم كل جديد", "شكرا للمشاهدة", "شكراً للمشاهدة",
        "شكرا لكم على المشاهدة", "شكراً لكم على المشاهدة", "Thank you for watching", "Thanks for watching", "Subtitles by", "you",
    ];

    /// <summary>بيشيل الجمل المؤلفة من النص. لو مفضلش غيرها بيرجّع نص فاضي.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text;
        if (Signatures.IsMatch(result))
        {
            result = Signatures.Replace(result, " ");
            result = Regex.Replace(result, @"[ \t]{2,}", " ");
            result = Regex.Replace(result, @"[ \t]+(\r?\n)", "$1");
            result = Regex.Replace(result, @"(\r?\n)[ \t]+", "$1");
            result = Regex.Replace(result, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
            result = result.Trim();
        }

        return IsOnlyHallucination(result) ? string.Empty : result;
    }

    /// <summary>بينضّف كل جزء، ويشيل الأجزاء اللي فضيت.</summary>
    public static IReadOnlyList<TranscriptSegment> Clean(IReadOnlyList<TranscriptSegment> segments)
    {
        List<TranscriptSegment>? cleaned = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var text = Clean(segments[i].Text);
            if (cleaned is null && !string.Equals(text, segments[i].Text, StringComparison.Ordinal))
            {
                cleaned = [.. segments.Take(i)];
            }

            if (cleaned is not null && text.Length > 0)
            {
                cleaned.Add(segments[i] with { Text = text });
            }
        }

        return cleaned ?? segments;
    }

    /// <summary>هل النص كله جملة مؤلفة؟</summary>
    public static bool IsOnlyHallucination(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // تسرب النص التوجيهي للنموذج المحلي عند السكوت
        if (text.Contains("هنشتغل على الـ API في Python", StringComparison.Ordinal))
        {
            return true;
        }

        var trimmed = text.Trim().Trim('(', ')', '[', ']', '«', '»', '"').Trim().TrimEnd('.', '!', '؟', '?', '،', ',').Trim();
        return WholeOnly.Any(h => string.Equals(trimmed, h, StringComparison.OrdinalIgnoreCase));
    }
}
