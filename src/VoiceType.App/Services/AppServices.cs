using System.IO;
using System.Net.Http;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Live;
using VoiceType.Providers.Rewrite;
using VoiceType.Providers.Resilience;
using VoiceType.Settings;
using VoiceType.Speech;
using VoiceType.Text;

namespace VoiceType.App.Services;

/// <summary>نموذج يمكن اختياره للإملاء أو لتفريغ الملفات.</summary>
public sealed record EngineOption(
    string Key,
    string Provider,
    string? Model,
    string Label,
    string Kind,
    string Pricing,
    bool Ready,
    string? Reason);

/// <summary>
/// قلب البرنامج: الإعدادات + المفاتيح + الخدمات + إنشاء المحركات.
/// </summary>
public sealed class AppServices : IDisposable
{
    private const string AdvancedProvider = "openai-compatible";
    private readonly Dictionary<string, ILocalEngine> _localEngines = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _localGate = new();
    private System.Threading.Timer? _unloadTimer;

    public AppServices()
    {
        LegacyMigration.RunOnce();
        _ = Task.Run(LegacyMigration.MoveLegacyModels);
        Catalog = ProviderCatalog.CreateDefault(AppPaths.CustomProviders);
        Keys = new SecureKeyStore();
        foreach (var definition in Catalog.All.Where(d => !string.IsNullOrWhiteSpace(d.KeyFrom)))
        {
            Keys.ShareKey(definition.Id, definition.KeyFrom!);
        }

        Settings = AppSettings.Load();
        Learned = new LearnedCorrections(AppPaths.LearnedFile);
        EnsureDefaults();
    }

    public ProviderCatalog Catalog { get; private set; }

    public SecureKeyStore Keys { get; }

    /// <summary>ما تعلّمه النموذج المحلي من Gemini.</summary>
    public LearnedCorrections Learned { get; }

    public AppSettings Settings { get; }

    public HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void ReloadCatalog() => Catalog = ProviderCatalog.CreateDefault(AppPaths.CustomProviders);

    public static bool IsLocal(string provider) =>
        string.Equals(provider, LocalModels.ProviderId, StringComparison.OrdinalIgnoreCase);

    public static bool NeedsKey(ProviderDefinition definition) =>
        definition.Request.Auth.Type != AuthDefinition.None && !definition.Request.Auth.Optional;

    public IEnumerable<ProviderDefinition> OnlineProviders =>
        Catalog.All
            .Where(d => !string.Equals(d.Id, AdvancedProvider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(PricingRank)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static int PricingRank(ProviderDefinition d) => d.Pricing?.ToLowerInvariant() switch
    {
        "free" => 0,
        "free-tier" => 1,
        "trial" => 2,
        "paid" => 4,
        _ => 3,
    };

    /// <summary>كل النماذج: على الجهاز أولًا، ثم خدمات الإنترنت (المجانية أولًا).</summary>
    public IReadOnlyList<EngineOption> Engines()
    {
        var options = new List<EngineOption>();
        foreach (var file in LocalModels.Installed())
        {
            options.Add(new EngineOption($"{LocalModels.ProviderId}|{file}", LocalModels.ProviderId, file,
                $"على جهازك · {LocalModels.DisplayNameOf(file)}", "local", "free", LocalModels.IsRunnable(file),
                LocalModels.IsRunnable(file) ? null : "محرك CrispASR مش متسطّب. نزّل النموذج تاني من الإعدادات."));
        }

        foreach (var definition in OnlineProviders)
        {
            var models = definition.Models.Select(m => m.Id)
                .Concat(Settings.ExtraModels.Where(m => string.Equals(m.Provider, definition.Id, StringComparison.OrdinalIgnoreCase)).Select(m => m.Model ?? string.Empty))
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (models.Count == 0 && definition.DefaultModel is { } fallbackModel)
            {
                models.Add(fallbackModel);
            }

            var ready = !NeedsKey(definition) || Keys.Has(definition.Id);
            foreach (var model in models)
            {
                options.Add(new EngineOption($"{definition.Id}|{model}", definition.Id, model,
                    $"{definition.Name} · {model}", "online", definition.Pricing ?? "unknown", ready,
                    ready ? null : "أضف المفتاح أولًا"));
            }
        }

        return options;
    }

    public EngineOption? Describe(EngineChoice? choice) =>
        choice is null ? null : Engines().FirstOrDefault(o => o.Key == choice.Key);

    public bool IsReady(EngineChoice? choice) => Describe(choice) is { Ready: true };

    /// <summary>يختار نموذجًا أساسيًا وبديلًا مناسبين إن لم يحددهما المستخدم.</summary>
    public void EnsureDefaults()
    {
        var engines = Engines();
        var changed = false;
        if (!IsReady(Settings.Primary))
        {
            var best = engines.FirstOrDefault(e => e.Ready && e.Kind == "online" && e.Pricing != "paid")
                       ?? engines.FirstOrDefault(e => e.Ready);
            if (best is not null)
            {
                Settings.Primary = EngineChoice.Parse(best.Key);
                changed = true;
            }
        }

        if (Settings.Fallback is null || !IsReady(Settings.Fallback) || Settings.Fallback.Same(Settings.Primary))
        {
            var local = engines.FirstOrDefault(e => e.Ready && e.Kind == "local" && e.Key != Settings.Primary?.Key)
                        ?? engines.FirstOrDefault(e => e.Ready && e.Key != Settings.Primary?.Key && e.Pricing != "paid");
            var fallback = local is null ? null : EngineChoice.Parse(local.Key);
            if (!(fallback?.Same(Settings.Fallback) ?? Settings.Fallback is null))
            {
                Settings.Fallback = fallback;
                changed = true;
            }
        }

        if (changed)
        {
            Settings.Save();
        }
    }

    /// <summary>
    /// ينشئ محركًا من اختيار. النماذج المحلية تبقى محمّلة لسرعة الاستجابة، وتُفرغ من الذاكرة بعد 30 دقيقة خمول.
    /// خدمات الإنترنت تستخدم كل مفاتيح الخدمة بالتناوب (مفتاح تجاوز حده أو رُفض ← التالي فورًا).
    /// </summary>
    public ISpeechEngine CreateEngine(EngineChoice choice, bool forDictation = false)
    {
        if (IsLocal(choice.Provider))
        {
            var file = choice.Model ?? throw new InvalidOperationException("لم يُحدد ملف النموذج.");
            lock (_localGate)
            {
                if (!_localEngines.TryGetValue(file, out var engine))
                {
                    engine = LocalModels.CreateEngine(file);
                    _localEngines[file] = engine;
                }

                ScheduleUnload();
                return new SharedEngine(engine);
            }
        }

        var definition = Catalog.Get(choice.Provider);
        var keys = Keys.List(definition.Id);
        if (keys.Count == 0)
        {
            // خدمة لا تحتاج مفتاحًا، أو لم يُضف مفتاح بعد (سيظهر خطأ واضح)
            var single = new ProviderInstance { Definition = definition.Id, Model = choice.Model, TimeoutSeconds = forDictation ? DictationTimeout : null };
            return new ResilientSpeechEngine(new DeclarativeSpeechEngine(definition, single, Http), ResilienceOptions.NoRetry);
        }

        var engines = keys.Select((key, index) => new KeyedEngine(
            KeyHealth.Fingerprint(definition.Id, key.Value),
            keys.Count == 1 ? "المفتاح" : $"المفتاح {index + 1}",
            new DeclarativeSpeechEngine(definition, new ProviderInstance
            {
                Definition = definition.Id,
                Model = choice.Model,
                ApiKey = key.Value,
                TimeoutSeconds = forDictation ? DictationTimeout : null,
            }, Http))).ToList();
        return new KeyRotatingEngine(engines);
    }

    /// <summary>أقصى انتظار لرد الخدمة في الإملاء قبل الانتقال للبديل (الملفات الطويلة لها مهلة أطول).</summary>
    private const int DictationTimeout = 40;

    /// <summary>يحمّل النماذج المحلية في سلسلة الإملاء مسبقًا (أثناء الكلام) حتى لا ينتظر المستخدم تحميلها.</summary>
    public void WarmUpLocal()
    {
        foreach (var choice in new[] { Settings.Primary, Settings.UseFallback ? Settings.Fallback : null })
        {
            if (choice is not null && IsLocal(choice.Provider) && IsReady(choice) && CreateEngine(choice) is SharedEngine shared)
            {
                _ = shared.PreloadAsync();
            }
        }
    }

    /// <summary>
    /// محرك الإملاء: خدمة الإنترنت الأساسية ← الخدمة الشقيقة ← النموذج المحلي.
    /// النموذج المحلي لا ينتظر حتى تنتهي مهلة الخدمات: إذا تأخرت بضع ثوانٍ يبدأ بالتوازي ويُكتب أسرع نص يصل.
    /// والخدمة التي تعطلت قبل قليل تُتخطى لدقائق بدل أن تضيع وقتًا في كل مرة.
    /// </summary>
    public ISpeechEngine CreateDictationEngine(TimeSpan? audioDuration = null)
    {
        EnsureDefaults();
        var primary = Settings.Primary ?? throw new InvalidOperationException("لم يُختر نموذج بعد. افتح البرنامج ونزّل نموذجًا أو أضف مفتاحًا.");
        var online = new List<ISpeechEngine> { CreateEngine(primary, forDictation: true) };

        // خدمة شقيقة مجانية بنفس المفتاح (مثل نموذج Gemini العام بجانب Gemini Transcribe):
        // تُجرب قبل النموذج المحلي، فإذا تعطّل أحدهما عند Google يعمل الآخر
        if (Settings.UseFallback && Companion(primary) is { } companion && IsReady(companion) && !companion.Same(Settings.Fallback))
        {
            online.Add(CreateEngine(companion, forDictation: true));
        }

        var fallback = Settings.UseFallback && Settings.Fallback is { } f && !f.Same(primary) && IsReady(f) ? f : null;

        if (fallback is not null && IsLocal(fallback.Provider) && !IsLocal(primary.Provider))
        {
            var chain = new FallbackSpeechEngine(online, "online", EngineHealth.Shared);
            return new HedgedSpeechEngine(chain, CreateEngine(fallback, forDictation: true), HedgeDelay(audioDuration));
        }

        if (fallback is not null)
        {
            online.Add(CreateEngine(fallback, forDictation: true));
        }

        return online.Count == 1 ? online[0] : new FallbackSpeechEngine(online);
    }

    /// <summary>
    /// كم ننتظر خدمة الإنترنت قبل تشغيل النموذج المحلي بالتوازي.
    /// Gemini يرد عادة خلال ثوانٍ قليلة؛ الكلام الأطول يأخذ وقتًا أطول قليلًا للرفع والتفريغ.
    /// </summary>
    public static TimeSpan HedgeDelay(TimeSpan? audio)
    {
        var seconds = 6 + ((audio?.TotalSeconds ?? 15) * 0.1);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 6, 15));
    }

    /// <summary>الصياغة والتلخيص بـ Gemini (مجاني بنفس المفتاح). null إذا لم يُحفظ مفتاح Gemini.</summary>
    /// <param name="preferredTimeout">أقصى انتظار لموديل Ollama / LM Studio قبل Gemini (قصير في الإملاء).</param>
    public TextRewriter? CreateRewriter(TimeSpan? preferredTimeout = null)
    {
        var keys = Keys.List("gemini")
            .OrderBy(k => KeyHealth.Shared.IsPaused(KeyHealth.Fingerprint("gemini", k.Value)) ? 1 : 0)
            .Select(k => k.Value)
            .ToList();
        var preferred = CreateTextEngine() is { } engine ? new List<ITextEngine> { engine } : [];
        return keys.Count == 0 && preferred.Count == 0 ? null : new TextRewriter(Http, keys, preferred: preferred) { PreferredTimeout = preferredTimeout };
    }

    /// <summary>دقة النماذج المحلية من الإعدادات المتقدمة (عرض البحث).</summary>
    public void ApplyLocalPrecision()
    {
        var beam = Math.Clamp(Settings.LocalBeamSize, 0, 8);
        WhisperLocalEngine.BeamOverride = beam;
        CrispLocalEngine.BeamSize = beam;
    }

    /// <summary>مفتاح Ollama Cloud (لو المستخدم ضافه). محفوظ مشفّر زي باقي المفاتيح.</summary>
    public const string OllamaCloudKeyId = "ollama-cloud";

    /// <summary>الموديل اللي المستخدم اختاره للملخص والصياغة (غير Gemini)، أو null.</summary>
    public ITextEngine? CreateTextEngine()
    {
        var model = Settings.TextAiModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        try
        {
            return Settings.TextAiProvider switch
            {
                "ollama" => new TextServerEngine(Http, TextServerKind.Ollama, Settings.OllamaUrl, model),
                "ollama-cloud" when Keys.Get(OllamaCloudKeyId) is { } key =>
                    new TextServerEngine(Http, TextServerKind.OllamaCloud, TextServerEngine.OllamaCloudUrl, model, key) { Timeout = TimeSpan.FromMinutes(3) },
                "lmstudio" => new TextServerEngine(Http, TextServerKind.OpenAiCompatible, Settings.LmStudioUrl, model),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null; // عنوان غلط في الإعدادات
        }
    }

    /// <summary>نوع الخادم والعنوان والمفتاح لمزود معيّن (لعرض الموديلات).</summary>
    public (TextServerKind Kind, string Url, string? Key)? TextServer(string provider) => provider switch
    {
        "ollama" => (TextServerKind.Ollama, Settings.OllamaUrl, null),
        "ollama-cloud" => (TextServerKind.OllamaCloud, TextServerEngine.OllamaCloudUrl, Keys.Get(OllamaCloudKeyId)),
        "lmstudio" => (TextServerKind.OpenAiCompatible, Settings.LmStudioUrl, null),
        _ => null,
    };

    public const string LiveEngineId = "gemini-live";

    private static readonly string[] LiveVocabulary =
    [
        "API", "Python", "VS Code", "Gmail", "ChatGPT", "Gemini", "GitHub", "Google", "Windows",
        "Excel", "Word", "WhatsApp", "email", "update", "bug", "Workspace",
    ];

    /// <summary>
    /// الكتابة المباشرة أثناء الكلام (Gemini Live): متاحة عندما يكون الأساسي من Gemini ومفتاحه محفوظ.
    /// إذا لم تتوفر (أو تعطلت قبل قليل) يعمل الإملاء بالطريقة العادية.
    /// </summary>
    public LiveTranscriber? CreateLiveTranscriber()
    {
        if (!Settings.LiveTyping || Settings.Primary is not { } primary || IsLocal(primary.Provider) ||
            !Catalog.TryGet(primary.Provider, out var definition) ||
            !string.Equals(definition.KeyFrom ?? definition.Id, "gemini", StringComparison.OrdinalIgnoreCase) ||
            EngineHealth.Shared.IsSkipped(LiveEngineId))
        {
            return null;
        }

        var keys = Keys.List("gemini")
            .OrderBy(k => KeyHealth.Shared.IsPaused(KeyHealth.Fingerprint("gemini", k.Value)) ? 1 : 0)
            .Select(k => k.Value)
            .ToList();
        if (keys.Count == 0)
        {
            return null;
        }

        string[] codes = Settings.Language switch
        {
            "ar" => ["ar-EG"],
            "en" => ["en-US"],
            _ => [],
        };
        var vocabulary = Learned.TopTerms(60).Concat(LiveVocabulary).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        return new LiveTranscriber(keys, new LiveOptions { LanguageCodes = codes, CustomVocabulary = vocabulary });
    }

    /// <summary>أول نموذج من خدمة مجانية تشارك نفس المفتاح مع الخدمة الأساسية.</summary>
    private EngineChoice? Companion(EngineChoice primary)
    {
        if (IsLocal(primary.Provider) || !Catalog.TryGet(primary.Provider, out var definition))
        {
            return null;
        }

        var owner = definition.KeyFrom ?? definition.Id;
        var sibling = Catalog.All.FirstOrDefault(d =>
            !string.Equals(d.Id, definition.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.KeyFrom ?? d.Id, owner, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(d.Pricing, "paid", StringComparison.OrdinalIgnoreCase));
        var model = sibling?.DefaultModel ?? sibling?.Models.FirstOrDefault()?.Id;
        return sibling is null || model is null ? null : new EngineChoice { Provider = sibling.Id, Model = model };
    }

    public TranscriptionOptions TranscriptionOptions => new()
    {
        Language = string.IsNullOrWhiteSpace(Settings.Language) ? null : Settings.Language,
        Prompt = string.Equals(Settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? null : LocalPrompt(),
    };

    /// <summary>نص التوجيه + أكثر الكلمات الإنجليزية التي تعلّمها البرنامج من Gemini.</summary>
    private string LocalPrompt()
    {
        var learned = Learned.TopTerms(25)
            .Where(term => !MixedLanguagePrompt.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(15)
            .ToList();
        return learned.Count == 0 ? MixedLanguagePrompt : MixedLanguagePrompt + " " + string.Join("، ", learned) + ".";
    }

    /// <summary>النموذج المحلي الذي يتعلم من Gemini (البديل إن كان محليًا، وإلا أول نموذج على الجهاز).</summary>
    public EngineChoice? LearningLocalModel()
    {
        if (Settings.Fallback is { } fallback && IsLocal(fallback.Provider) && IsReady(fallback))
        {
            return fallback;
        }

        var file = LocalModels.Installed().FirstOrDefault();
        return file is null ? null : new EngineChoice { Provider = LocalModels.ProviderId, Model = file };
    }

    /// <summary>
    /// نص توجيهي للنموذج المحلي (Whisper): كلام مصري فيه كلمات إنجليزية مكتوبة بحروفها،
    /// فيتعلم النموذج من المثال أن يكتب الكلمات الإنجليزية بالإنجليزي بدل نطقها بحروف عربية.
    /// </summary>
    public const string MixedLanguagePrompt =
        "النهارده هنشتغل على الـ API في Python و VS Code، وابعتلي الـ email على Gmail، وجرّبت ChatGPT و Gemini.";

    public LineBreakMode LineBreaks => Enum.TryParse<LineBreakMode>(Settings.LineBreaks, ignoreCase: true, out var mode) ? mode : LineBreakMode.Sentences;

    public TextMode TextMode => Enum.TryParse<TextMode>(Settings.TextMode, ignoreCase: true, out var mode) ? mode : TextMode.General;

    private void ScheduleUnload()
    {
        _unloadTimer ??= new System.Threading.Timer(_ => UnloadLocalModels());
        _unloadTimer.Change(TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
    }

    /// <summary>يفرغ النماذج المحلية غير المشغولة (المشغول يُعاد فحصه بعد 5 دقائق).</summary>
    public void UnloadLocalModels()
    {
        lock (_localGate)
        {
            var busy = false;
            foreach (var engine in _localEngines.Values)
            {
                busy |= !engine.TryUnload();
            }

            if (busy)
            {
                _unloadTimer?.Change(TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>قبل حذف ملف نموذج: ينتظر انتهاء استخدامه ثم يحرره.</summary>
    public void ReleaseLocalModel(string file)
    {
        lock (_localGate)
        {
            if (_localEngines.TryGetValue(file, out var engine))
            {
                if (!engine.TryUnload())
                {
                    throw new InvalidOperationException("هذا النموذج يعمل الآن (تفريغ جارٍ). انتظر حتى ينتهي ثم احذفه.");
                }

                _localEngines.Remove(file);
            }
        }
    }

    public void Dispose()
    {
        _unloadTimer?.Dispose();
        lock (_localGate)
        {
            foreach (var engine in _localEngines.Values)
            {
                engine.TryUnload(); // عند الخروج لا ننتظر تفريغًا جاريًا
            }
        }

        Http.Dispose();
    }

    /// <summary>غلاف لا يُفرغ النموذج المحلي المشترك عند انتهاء عملية واحدة.</summary>
    internal sealed class SharedEngine(ILocalEngine inner) : ISpeechEngine, ISegmentedSpeechEngine
    {
        public string Id => inner.Id;

        public string DisplayName => inner.DisplayName;

        public SpeechCapabilities Capabilities => inner.Capabilities;

        public Task<TranscriptionResult> TranscribeAsync(AudioPayload audio, TranscriptionOptions options, CancellationToken cancellationToken = default) =>
            inner.TranscribeAsync(audio, options, cancellationToken);

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(float[] samples16kMono, TranscriptionOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            inner.TranscribeSegmentsAsync(samples16kMono, options, progress, cancellationToken);

        public Task PreloadAsync() => inner.PreloadAsync();
    }
}
