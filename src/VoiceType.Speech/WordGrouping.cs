using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// تجميع الكلمات (اللي ليها وقت بداية ونهاية) في جمل قصيرة: عند كل وقفة، أو نهاية جملة، أو بعد ثواني قليلة.
/// الجمل القصيرة مناسبة لترجمات الفيديو (SRT/VTT)، ولمعرفة مين اللي قال كل جملة.
/// </summary>
public static class WordGrouping
{
    public readonly record struct Word(TimeSpan Start, TimeSpan End, string Text);

    /// <summary>
    /// أطول وقت للكلمة الواحدة. النموذج أحيانًا بيمدّ آخر كلمة قبل السكوت لحد الكلمة اللي بعدها،
    /// فتاخد السكوت (وأحيانًا أول كلام المتكلم التاني). الكلمة نادرًا بتعدّي ثانية.
    /// </summary>
    public const double MaxWordSeconds = 1.0;

    public static IReadOnlyList<TranscriptSegment> Group(IReadOnlyList<Word> words, double maxPauseSeconds = 0.35, double maxSeconds = 5)
    {
        var result = new List<TranscriptSegment>();
        var current = new List<Word>();
        foreach (var raw in words)
        {
            var longest = raw.Start + TimeSpan.FromSeconds(MaxWordSeconds);
            var word = raw.End > longest ? raw with { End = longest } : raw;
            if (current.Count > 0)
            {
                var pause = (word.Start - current[^1].End).TotalSeconds;
                var length = (word.End - current[0].Start).TotalSeconds;
                if (pause > maxPauseSeconds || length > maxSeconds || EndsSentence(current[^1].Text))
                {
                    Flush();
                }
            }

            current.Add(word);
        }

        Flush();
        return result;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            result.Add(new TranscriptSegment(current[0].Start, current[^1].End, string.Join(" ", current.Select(w => w.Text))));
            current.Clear();
        }
    }

    /// <summary>
    /// النموذج أحيانًا بيرجّع الكلمات من غير علامات الترقيم اللي في النص الكامل.
    /// لو عدد كلمات النص زي عدد الكلمات اللي ليها وقت، ناخد الكلمة من النص (بفواصلها ونقطها).
    /// </summary>
    public static IReadOnlyList<Word> WithPunctuation(IReadOnlyList<Word> words, string fullText)
    {
        var tokens = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != words.Count)
        {
            return words;
        }

        return words.Select((w, i) => w with { Text = tokens[i] }).ToList();
    }

    private static bool EndsSentence(string word) =>
        word.Length > 0 && word[^1] is '.' or '؟' or '?' or '!' or '۔';
}
