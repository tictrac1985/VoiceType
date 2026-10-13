using System.Reflection;
using VoiceType.Core.Speech;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Resilience;

namespace VoiceType.Providers;

/// <summary>
/// كتالوج تعريفات المزودات: المدمجة مع البرنامج + ملفات المستخدم المخصصة.
/// تعريف المستخدم بنفس المعرّف يحل محل التعريف المدمج.
/// </summary>
public sealed class ProviderCatalog
{
    private const string ResourcePrefix = "VoiceType.Providers.Catalog.";
    private readonly Dictionary<string, ProviderDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ProviderDefinition> All =>
        _definitions.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>أخطاء ملفات التعريف المخصصة التي لم تُحمّل.</summary>
    public List<string> LoadErrors { get; } = [];

    public static ProviderCatalog CreateDefault(string? customDirectory = null)
    {
        var catalog = new ProviderCatalog();
        catalog.LoadBuiltIn();
        if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            catalog.LoadDirectory(customDirectory);
        }

        return catalog;
    }

    public void LoadBuiltIn()
    {
        var assembly = typeof(ProviderCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            Add(ProviderDefinitionSerializer.Deserialize(reader.ReadToEnd()), "built-in");
        }
    }

    public void LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Add(ProviderDefinitionSerializer.Deserialize(File.ReadAllText(file)), file);
            }
            catch (ProviderDefinitionException ex)
            {
                LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public void Add(ProviderDefinition definition, string source = "custom")
    {
        _definitions[definition.Id] = definition;
        _sources[definition.Id] = source;
    }

    public bool TryGet(string id, out ProviderDefinition definition) =>
        _definitions.TryGetValue(id, out definition!);

    public ProviderDefinition Get(string id) =>
        TryGet(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Provider '{id}' is not in the catalog.");

    public string GetSource(string id) => _sources.TryGetValue(id, out var source) ? source : "unknown";

    /// <summary>ينشئ محركًا جاهزًا من نسخة المستخدم، مع إعادة المحاولة والـCircuit breaker.</summary>
    public ISpeechEngine CreateEngine(ProviderInstance instance, HttpClient http, ResilienceOptions? resilience = null)
    {
        var engine = new DeclarativeSpeechEngine(Get(instance.Definition), instance, http);
        return resilience is { MaxAttempts: <= 1, CircuitBreakerThreshold: int.MaxValue }
            ? engine
            : new ResilientSpeechEngine(engine, resilience);
    }

    public static string BuiltInVersion =>
        typeof(ProviderCatalog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
}
