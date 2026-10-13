using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceType.Text;

/// <summary>
/// قاموس مصطلحات: "بايثون" ← "Python".
/// المطابقة على كلمات كاملة فقط، وتتسامح مع اختلاف كتابة الألف (ا أ إ آ) والياء (ي ى) والتاء المربوطة (ة ه).
/// </summary>
public sealed class TermDictionary
{
    private readonly List<(string From, string To, Regex Pattern)> _entries = [];

    public TermDictionary(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (from, to) in entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                     .OrderByDescending(e => e.Key.Length))
        {
            _entries.Add((from.Trim(), to, BuildPattern(from.Trim())));
        }
    }

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<string, string>> Entries =>
        _entries.Select(e => new KeyValuePair<string, string>(e.From, e.To));

    public static TermDictionary Empty { get; } = new([]);

    /// <summary>قاموس المصطلحات التقنية الافتراضي المدمج مع البرنامج.</summary>
    public static TermDictionary LoadTechnicalDefault() => LoadEmbedded("technical-ar.json");

    public static TermDictionary FromJson(string json)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? [];
        return new TermDictionary(map);
    }

    public static TermDictionary FromFile(string path) =>
        File.Exists(path) ? FromJson(File.ReadAllText(path)) : Empty;

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var (_, to, pattern) in _entries)
        {
            text = pattern.Replace(text, to.Replace("$", "$$", StringComparison.Ordinal));
        }

        return text;
    }

    private static TermDictionary LoadEmbedded(string fileName)
    {
        var assembly = typeof(TermDictionary).Assembly;
        using var stream = assembly.GetManifestResourceStream($"VoiceType.Text.Dictionaries.{fileName}")
            ?? throw new InvalidOperationException($"Embedded dictionary '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private static Regex BuildPattern(string from)
    {
        var builder = new StringBuilder(@"(?<![\p{L}\p{N}_])");
        for (var i = 0; i < from.Length; i++)
        {
            var c = from[i];
            var atWordEnd = i == from.Length - 1 || char.IsWhiteSpace(from[i + 1]);
            builder.Append(c switch
            {
                'ا' or 'أ' or 'إ' or 'آ' => "[اأإآ]",
                'ي' or 'ى' when atWordEnd => "[يى]",
                'ة' or 'ه' when atWordEnd => "[ةه]",
                _ when char.IsWhiteSpace(c) => @"\s+",
                _ => Regex.Escape(c.ToString()),
            });
        }

        builder.Append(@"(?![\p{L}\p{N}_])");
        var ignoreCase = from.Any(ArabicText.IsLatinLetter) ? RegexOptions.IgnoreCase : RegexOptions.None;
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | ignoreCase);
    }
}
