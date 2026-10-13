using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoiceType.Providers.Templating;

/// <summary>
/// قيم المتغيرات المتاحة داخل قوالب ملف التعريف.
/// </summary>
public sealed class TemplateContext
{
    public const string Model = "model";
    public const string Language = "language";
    public const string Prompt = "prompt";
    public const string Temperature = "temperature";
    public const string AudioBase64 = "audio_base64";
    public const string AudioFormat = "audio_format";
    public const string AudioMime = "audio_mime";
    public const string FileName = "file_name";
    public const string ApiKey = "api_key";

    public static readonly IReadOnlySet<string> BuiltInNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Model, Language, Prompt, Temperature, AudioBase64, AudioFormat, AudioMime, FileName, ApiKey,
    };

    private readonly Dictionary<string, Func<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public TemplateContext Set(string name, string? value)
    {
        var captured = value ?? string.Empty;
        _values[name] = () => captured;
        return this;
    }

    /// <summary>قيمة تُحسب عند الحاجة فقط (مثل base64 للصوت).</summary>
    public TemplateContext SetLazy(string name, Func<string> factory)
    {
        var lazy = new Lazy<string>(factory);
        _values[name] = () => lazy.Value;
        return this;
    }

    public string Get(string name) => _values.TryGetValue(name, out var value) ? value() : string.Empty;
}

public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::(number|bool|string))?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^\{([A-Za-z_][A-Za-z0-9_]*)(?::(number|bool|string))?\}$")]
    private static partial Regex WholePlaceholderRegex();

    public static IEnumerable<string> GetVariableNames(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value);

    /// <summary>يعوّض المتغيرات في نص. المتغير غير الموجود يصبح نصًا فارغًا.</summary>
    public static string Render(string template, TemplateContext context) =>
        PlaceholderRegex().Replace(template, m => context.Get(m.Groups[1].Value));

    /// <summary>
    /// يعوّض المتغيرات داخل قالب JSON.
    /// - القيمة التي تتكون من متغير واحد فقط تأخذ نوعه: {temperature:number} يصبح رقمًا.
    /// - الحقول التي تصبح فارغة تُحذف، وكذلك الكائنات التي تفرغ بالكامل.
    /// </summary>
    public static JsonNode? RenderJson(JsonNode? template, TemplateContext context)
    {
        var (keep, node) = RenderNode(template, context);
        return keep ? node : null;
    }

    private static (bool Keep, JsonNode? Node) RenderNode(JsonNode? node, TemplateContext context)
    {
        switch (node)
        {
            case null:
                return (true, null);

            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var (keep, rendered) = RenderNode(value, context);
                    if (keep)
                    {
                        result[key] = rendered;
                    }
                }

                return (result.Count > 0 || obj.Count == 0, result);
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    var (keep, rendered) = RenderNode(item, context);
                    if (keep)
                    {
                        result.Add(rendered);
                    }
                }

                return (true, result);
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return RenderString(text, context);

            default:
                return (true, node.DeepClone());
        }
    }

    private static (bool Keep, JsonNode? Node) RenderString(string text, TemplateContext context)
    {
        var whole = WholePlaceholderRegex().Match(text);
        if (!whole.Success)
        {
            var rendered = Render(text, context);
            return string.IsNullOrEmpty(rendered) ? (false, null) : (true, JsonValue.Create(rendered));
        }

        var raw = context.Get(whole.Groups[1].Value);
        if (string.IsNullOrEmpty(raw))
        {
            return (false, null);
        }

        return whole.Groups[2].Value switch
        {
            "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? (true, JsonValue.Create(number))
                : (false, null),
            "bool" => bool.TryParse(raw, out var flag)
                ? (true, JsonValue.Create(flag))
                : (false, null),
            _ => (true, JsonValue.Create(raw)),
        };
    }
}
