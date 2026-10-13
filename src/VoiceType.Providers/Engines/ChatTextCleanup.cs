using System.Text.RegularExpressions;

namespace VoiceType.Providers.Engines;

/// <summary>ينظف رد نموذج لغوي طُلب منه التفريغ، فيبقى نص الكلام فقط.</summary>
public static partial class ChatTextCleanup
{
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkRegex();

    [GeneratedRegex(@"^```[a-zA-Z]*\s*|\s*```$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s*(?:\*\*)?(?:transcript(?:ion)?|the transcript(?:ion)? is|here is the transcript(?:ion)?|النص|التفريغ|نص الكلام|النص المفرغ)(?:\*\*)?\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    public static string Clean(string text)
    {
        var result = ThinkRegex().Replace(text, string.Empty);
        var open = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            result = result[..open];
        }

        result = FenceRegex().Replace(result.Trim(), string.Empty).Trim();
        result = LabelRegex().Replace(result, string.Empty).Trim();
        if (result.Length >= 2 && ((result[0] == '"' && result[^1] == '"') || (result[0] == '«' && result[^1] == '»') || (result[0] == '“' && result[^1] == '”')))
        {
            result = result[1..^1].Trim();
        }

        return result;
    }
}
