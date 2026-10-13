using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Settings;
using VoiceType.Speech;

namespace VoiceType.Evaluation.Comparison;

/// <summary>
/// كل ما يخص المقارنة في مكان واحد: المزودات + المفاتيح المشفرة + النماذج المختارة.
/// برنامج المختبر وأداة سطر الأوامر يستخدمان هذا الكلاس نفسه، فأي تغيير من أحدهما يظهر في الآخر.
/// </summary>
public sealed class LabWorkspace
{
    /// <summary>مزودات لا تظهر في قائمة المفاتيح لأنها تحتاج إعدادًا متقدمًا (عنوان خادم).</summary>
    private static readonly HashSet<string> AdvancedProviders = new(StringComparer.OrdinalIgnoreCase) { "openai-compatible" };

    private LabWorkspace(ProviderCatalog catalog, SecureKeyStore keys, LabSettings settings)
    {
        Catalog = catalog;
        Keys = keys;
        Settings = settings;
        foreach (var definition in catalog.All.Where(d => !string.IsNullOrWhiteSpace(d.KeyFrom)))
        {
            keys.ShareKey(definition.Id, definition.KeyFrom!);
        }
    }

    public ProviderCatalog Catalog { get; private set; }

    public SecureKeyStore Keys { get; }

    public LabSettings Settings { get; private set; }

    public static LabWorkspace Open()
    {
        var workspace = new LabWorkspace(
            ProviderCatalog.CreateDefault(AppPaths.CustomProviders),
            new SecureKeyStore(),
            LabSettings.Load());
        workspace.EnsureModels();
        return workspace;
    }

    public void Reload()
    {
        Catalog = ProviderCatalog.CreateDefault(AppPaths.CustomProviders);
        Settings = LabSettings.Load();
        EnsureModels();
    }

    public static bool NeedsKey(ProviderDefinition definition) =>
        definition.Request.Auth.Type != AuthDefinition.None && !definition.Request.Auth.Optional;

    /// <summary>المزودات التي تظهر في شاشة المفاتيح: المجانية أولًا ثم المدفوعة.</summary>
    public IReadOnlyList<ProviderDefinition> KeyProviders =>
        Catalog.All
            .Where(d => NeedsKey(d) && !AdvancedProviders.Contains(d.Id))
            .OrderBy(d => PricingRank(d))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static int PricingRank(ProviderDefinition definition) => definition.Pricing?.ToLowerInvariant() switch
    {
        "free" => 0,
        "free-tier" => 1,
        "trial" => 2,
        "paid" => 4,
        _ => 3,
    };

    public static bool IsLocal(string providerId) =>
        string.Equals(providerId, LocalModels.ProviderId, StringComparison.OrdinalIgnoreCase);

    public string ProviderName(string providerId) =>
        IsLocal(providerId) ? "على جهازك" : Catalog.TryGet(providerId, out var d) ? d.Name : providerId;

    public string ModelLabel(LabModel model) =>
        IsLocal(model.Provider)
            ? $"على جهازك · {LocalModels.DisplayNameOf(model.Model ?? string.Empty)}"
            : $"{ProviderName(model.Provider)} · {model.Model ?? "default"}";

    public bool HasKey(string providerId) => Keys.Has(providerId);

    public bool IsReady(LabModel model)
    {
        if (IsLocal(model.Provider))
        {
            return model.Model is not null && LocalModels.IsInstalled(model.Model);
        }

        if (!Catalog.TryGet(model.Provider, out var definition))
        {
            return false;
        }

        if (NeedsKey(definition))
        {
            return HasKey(model.Provider);
        }

        // المزود المتقدم (مثل خادم محلي) يحتاج عنوانًا خاصًا بهذا النموذج
        return !AdvancedProviders.Contains(definition.Id) || model.Variables is { Count: > 0 };
    }

    public void SaveKey(string providerId, string? apiKey)
    {
        Keys.Set(providerId, apiKey);
    }

    public async Task<ConnectionTestResult> TestKeyAsync(string providerId, HttpClient http, CancellationToken cancellationToken = default)
    {
        var definition = Catalog.Get(providerId);
        var key = Keys.Get(providerId);
        if (NeedsKey(definition) && key is null)
        {
            return new ConnectionTestResult(ConnectionStatus.Failed, "✗ لا يوجد مفتاح محفوظ لهذا المزود.");
        }

        return await ConnectionTester.TestAsync(definition, key, http, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>النماذج المفعّلة والجاهزة (لها مفتاح).</summary>
    public IReadOnlyList<CompareTarget> ActiveTargets() =>
        Settings.Models
            .Where(m => m.Enabled && IsReady(m))
            .Select(m => new CompareTarget(
                m.Provider,
                m.Model,
                IsLocal(m.Provider) ? null : Keys.Get(m.Provider),
                Label: ModelLabel(m),
                Variables: m.Variables))
            .ToList();

    /// <summary>ينشئ أداة المقارنة، مع دعم النماذج المحلية على الجهاز.</summary>
    public ComparisonRunner CreateRunner(HttpClient http) =>
        new(Catalog, http, target =>
            target.Model is not null && LocalModels.IsInstalled(target.Model)
                ? new WhisperLocalEngine(LocalModels.PathOf(target.Model), displayName: target.Label)
                : null);

    /// <summary>يضيف نموذجًا محليًا (بعد تنزيله أو نسخه) إلى قائمة المقارنة.</summary>
    public void RegisterLocalModel(string fileName)
    {
        Settings.Add(LocalModels.ProviderId, fileName);
        Settings.Save();
    }

    public void UnregisterLocalModel(string fileName)
    {
        Settings.Remove(LocalModels.ProviderId, fileName);
        Settings.Save();
    }

    /// <summary>
    /// يضيف مزودًا مخصصًا من ملف JSON أو نص JSON، بعد التحقق منه، ويضيف نماذجه للمقارنة.
    /// </summary>
    public ProviderDefinition AddCustomProvider(string jsonOrPath)
    {
        var json = File.Exists(jsonOrPath) ? File.ReadAllText(jsonOrPath) : jsonOrPath;
        var definition = ProviderDefinitionSerializer.Deserialize(json);

        AppPaths.Ensure(AppPaths.CustomProviders);
        File.WriteAllText(Path.Combine(AppPaths.CustomProviders, definition.Id + ".json"), json);
        Catalog.Add(definition, "custom");

        AddModelsOf(definition);
        if (!Settings.KnownProviders.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
        {
            Settings.KnownProviders.Add(definition.Id);
        }

        Settings.Save();
        return definition;
    }

    public bool RemoveCustomProvider(string providerId)
    {
        var path = Path.Combine(AppPaths.CustomProviders, providerId + ".json");
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        Settings.Models.RemoveAll(m => string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase));
        Settings.KnownProviders.RemoveAll(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Reload();
        return true;
    }

    public string NewResultsFolder() =>
        AppPaths.Ensure(Path.Combine(AppPaths.LabResults, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")));

    public TranscriptionOptions TranscriptionOptions => new()
    {
        Language = string.IsNullOrWhiteSpace(Settings.Language) ? null : Settings.Language,
    };

    private void EnsureModels()
    {
        var changed = false;
        foreach (var definition in Catalog.All.Where(d => !AdvancedProviders.Contains(d.Id)))
        {
            // نضيف نماذج المزود مرة واحدة فقط عند ظهوره أول مرة، حتى لا نعيد ما حذفه المستخدم
            if (!Settings.KnownProviders.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
            {
                AddModelsOf(definition);
                Settings.KnownProviders.Add(definition.Id);
                changed = true;
            }
        }

        // النماذج المحلية الموجودة على الجهاز تظهر تلقائيًا في المقارنة
        foreach (var file in LocalModels.Installed())
        {
            if (Settings.Find(LocalModels.ProviderId, file) is null)
            {
                Settings.Add(LocalModels.ProviderId, file);
                changed = true;
            }
        }

        // ترقية الإعدادات القديمة: النماذج المدفوعة لا تُستخدم إلا إذا فعّلها المستخدم بنفسه
        if (Settings.Version < 2)
        {
            foreach (var model in Settings.Models)
            {
                if (Catalog.TryGet(model.Provider, out var definition) && definition.IsPaid)
                {
                    model.Enabled = false;
                }
            }

            Settings.Version = 2;
            changed = true;
        }

        if (changed || !File.Exists(LabSettings.FilePath))
        {
            Settings.Save();
        }
    }

    private bool AddModelsOf(ProviderDefinition definition)
    {
        var changed = false;
        var enabled = !definition.IsPaid;
        if (definition.Models.Count == 0)
        {
            changed |= Settings.Add(definition.Id, definition.DefaultModel, enabled);
        }

        foreach (var model in definition.Models)
        {
            changed |= Settings.Add(definition.Id, model.Id, enabled);
        }

        return changed;
    }
}
