using System.Globalization;
using System.Text.Json.Nodes;

namespace VoiceType.Providers.Templating;

/// <summary>
/// مسارات مبسطة لاستخراج قيمة من رد JSON:
/// text — error.message — results.channels[0].alternatives[0].transcript — choices[0].message.content
/// </summary>
public static class JsonPathLite
{
    public static JsonNode? Select(JsonNode? root, string path)
    {
        if (root is null)
        {
            return null;
        }

        // بدائل: "output_text|steps[-1].content[0].text" — أول مسار يوجد يُستخدم
        if (path.Contains('|', StringComparison.Ordinal))
        {
            foreach (var alternative in path.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (SelectSingle(root, alternative) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return SelectSingle(root, path);
    }

    private static JsonNode? SelectSingle(JsonNode root, string path)
    {
        var trimmed = path.Trim();
        if (trimmed is "" or "$")
        {
            return root;
        }

        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        var current = root;
        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket >= 0 ? segment[..bracket] : segment;

            if (name.Length > 0)
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current) || current is null)
                {
                    return null;
                }
            }

            while (bracket >= 0)
            {
                var close = segment.IndexOf(']', bracket);
                if (close < 0 ||
                    !int.TryParse(segment.AsSpan(bracket + 1, close - bracket - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) ||
                    current is not JsonArray array)
                {
                    return null;
                }

                // الفهرس السالب من النهاية: [-1] = آخر عنصر
                if (index < 0)
                {
                    index += array.Count;
                }

                if (index < 0 || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
                if (current is null)
                {
                    return null;
                }

                bracket = segment.IndexOf('[', close);
            }
        }

        return current;
    }

    /// <summary>
    /// يعيد القيمة كنص. المصفوفة من النصوص (مقاطع) تُدمج بمسافة.
    /// </summary>
    public static string? SelectString(JsonNode? root, string path)
    {
        var node = Select(root, path);
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value => value.ToJsonString(),
            JsonArray array => string.Join(' ', array.Select(item => item switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o when o["text"] is JsonValue t && t.TryGetValue<string>(out var s) => s,
                _ => null,
            }).Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => null,
        };
    }

    public static double? SelectNumber(JsonNode? root, string path)
    {
        var node = Select(root, path);
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }
}
