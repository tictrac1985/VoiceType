using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceType.Evaluation;

public sealed record TranscriptScore(
    int ReferenceWords,
    int WordErrors,
    int ReferenceChars,
    int CharErrors,
    int LatinTermsTotal,
    int LatinTermsFound)
{
    /// <summary>نسبة خطأ الكلمات (WER) بعد التوحيد.</summary>
    public double Wer => ReferenceWords == 0 ? 0 : (double)WordErrors / ReferenceWords;

    /// <summary>نسبة خطأ الحروف (CER) بعد التوحيد.</summary>
    public double Cer => ReferenceChars == 0 ? 0 : (double)CharErrors / ReferenceChars;

    /// <summary>نسبة المصطلحات الإنجليزية التي كُتبت بحروف إنجليزية صحيحة. null إن لم توجد مصطلحات.</summary>
    public double? LatinTermAccuracy => LatinTermsTotal == 0 ? null : (double)LatinTermsFound / LatinTermsTotal;
}

/// <summary>
/// تقييم التفريغ مقابل النص الصحيح.
/// التوحيد المستخدم هو المعتاد في تقييم العربية: حذف التشكيل والتطويل وعلامات الترقيم،
/// وتوحيد أشكال الألف والياء والتاء المربوطة، وتحويل الأرقام الهندية إلى عربية (0-9).
/// </summary>
public static partial class TranscriptScorer
{
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9.+#\-]*[A-Za-z0-9+#]|[A-Za-z]")]
    private static partial Regex LatinTermRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static TranscriptScore Score(string reference, string hypothesis)
    {
        var refNorm = Normalize(reference);
        var hypNorm = Normalize(hypothesis);

        var refWords = Tokenize(refNorm);
        var hypWords = Tokenize(hypNorm);
        var refChars = refNorm.Replace(" ", string.Empty, StringComparison.Ordinal);
        var hypChars = hypNorm.Replace(" ", string.Empty, StringComparison.Ordinal);

        var (latinTotal, latinFound) = ScoreLatinTerms(reference, hypothesis);

        return new TranscriptScore(
            refWords.Length,
            EditDistance(refWords, hypWords),
            refChars.Length,
            EditDistance(refChars.ToCharArray(), hypChars.ToCharArray()),
            latinTotal,
            latinFound);
    }

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            switch (c)
            {
                // التشكيل والتطويل
                case >= 'ً' and <= 'ٟ':
                case 'ٰ':
                case 'ـ':
                    continue;
                case 'أ' or 'إ' or 'آ' or 'ٱ':
                    builder.Append('ا');
                    continue;
                case 'ى':
                    builder.Append('ي');
                    continue;
                case 'ة':
                    builder.Append('ه');
                    continue;
                case >= '٠' and <= '٩':
                    builder.Append((char)('0' + (c - '٠')));
                    continue;
                case >= '۰' and <= '۹':
                    builder.Append((char)('0' + (c - '۰')));
                    continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsPunctuation(c) || char.IsSymbol(c) || category == UnicodeCategory.Format)
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    private static string[] Tokenize(string normalized) =>
        normalized.Length == 0 ? [] : normalized.Split(' ');

    /// <summary>
    /// هل كُتبت المصطلحات الإنجليزية في النص الصحيح بحروف إنجليزية في التفريغ؟
    /// هذا أهم مقياس لمشكلة "بايثون ← Python".
    /// </summary>
    private static (int Total, int Found) ScoreLatinTerms(string reference, string hypothesis)
    {
        var expected = LatinTermRegex().Matches(reference).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (expected.Count == 0)
        {
            return (0, 0);
        }

        var available = LatinTermRegex().Matches(hypothesis)
            .Select(m => m.Value.ToLowerInvariant())
            .GroupBy(v => v)
            .ToDictionary(g => g.Key, g => g.Count());

        var found = 0;
        foreach (var term in expected)
        {
            if (available.TryGetValue(term, out var count) && count > 0)
            {
                found++;
                available[term] = count - 1;
            }
        }

        return (expected.Count, found);
    }

    internal static int EditDistance<T>(IReadOnlyList<T> source, IReadOnlyList<T> target)
    {
        var comparer = EqualityComparer<T>.Default;
        var previous = new int[target.Count + 1];
        var current = new int[target.Count + 1];
        for (var j = 0; j <= target.Count; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= source.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Count; j++)
            {
                var cost = comparer.Equals(source[i - 1], target[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Count];
    }
}
