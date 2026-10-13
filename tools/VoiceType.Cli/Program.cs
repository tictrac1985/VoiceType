using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using VoiceType.Evaluation.Comparison;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Settings;
using VoiceType.Speech;

// voicetype-cli — أداة سطر أوامر لإدارة VoiceType Lab (للمطور وللوكيل البرمجي).
// تشارك المفاتيح المشفرة والإعدادات مع برنامج VoiceType Lab.
// المخرجات بالإنجليزية لتسهيل قراءتها آليًا، و--json متاح لأوامر العرض.

Console.OutputEncoding = Encoding.UTF8;
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var asJson = args.Contains("--json");
string Arg(int index) => index < positional.Count ? positional[index] : string.Empty;

try
{
    return (Arg(0).ToLowerInvariant(), Arg(1).ToLowerInvariant()) switch
    {
        ("paths", _) => Paths(),
        ("providers", "list") => ProvidersList(),
        ("providers", "show") => ProvidersShow(Arg(2)),
        ("providers", "add") => ProvidersAdd(Arg(2)),
        ("providers", "remove") => ProvidersRemove(Arg(2)),
        ("keys", "list") => KeysList(),
        ("keys", "set") => await KeysSet(Arg(2), Arg(3)),
        ("keys", "test") => await KeysTest(Arg(2)),
        ("keys", "add") => KeysAdd(Arg(2), Arg(3)),
        ("keys", "remove") => KeysRemove(Arg(2)),
        ("models", "list") => ModelsList(),
        ("models", "add") => ModelsAdd(Arg(2), Arg(3)),
        ("models", "remove") => ModelsRemove(Arg(2), Arg(3)),
        ("models", "enable") => ModelsEnable(Arg(2), Arg(3), true),
        ("models", "disable") => ModelsEnable(Arg(2), Arg(3), false),
        ("settings", "show") => SettingsShow(),
        ("settings", "language") => SettingsLanguage(Arg(2)),
        ("settings", "audio-folder") => SettingsAudioFolder(Arg(2)),
        ("samples", "list") => SamplesList(),
        ("local", "list") => LocalList(),
        ("local", "download") => await LocalDownload(Arg(2)),
        ("local", "add-file") => LocalAddFile(Arg(2)),
        ("local", "remove") => LocalRemove(Arg(2)),
        ("local", "gpu") => await LocalGpu(),
        ("compare", "preview") => ComparePreview(),
        ("compare", "run") => await CompareRun(),
        ("app", "show") => AppShow(),
        ("app", "primary") => AppSet("primary", Arg(2), Arg(3)),
        ("app", "fallback") => AppSet("fallback", Arg(2), Arg(3)),
        ("app", "files") => AppSet("files", Arg(2), Arg(3)),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is ProviderDefinitionException or KeyNotFoundException or IOException or PlatformNotSupportedException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

LabWorkspace Open() => LabWorkspace.Open();

// ===== إعدادات البرنامج الرئيسي VoiceType.exe =====
void AddToApp(ProviderDefinition definition, string model)
{
    if (model.Length == 0 || definition.Models.Any(m => m.Id == model))
    {
        return;
    }

    var app = AppSettings.Load();
    var choice = new EngineChoice { Provider = definition.Id, Model = model };
    if (!app.ExtraModels.Any(m => m.Same(choice)))
    {
        app.ExtraModels.Add(choice);
        app.Save();
    }
}

int AppShow()
{
    var app = AppSettings.Load();
    Console.WriteLine($"primary  : {app.Primary?.Key ?? "(none)"}");
    Console.WriteLine($"fallback : {app.Fallback?.Key ?? "(none)"}  (use fallback: {app.UseFallback})");
    Console.WriteLine($"files    : {app.FilesEngine?.Key ?? "(same as primary)"}");
    Console.WriteLine($"language : {app.Language ?? "auto"}   text mode: {app.TextMode}");
    Console.WriteLine($"extra models: {(app.ExtraModels.Count == 0 ? "(none)" : string.Join(", ", app.ExtraModels.Select(m => m.Key)))}");
    Console.WriteLine($"local models installed: {string.Join(", ", LocalModels.Installed())}");
    return 0;
}

int AppSet(string what, string providerId, string model)
{
    if (providerId.Length == 0 || model.Length == 0)
    {
        return Fail($"usage: voicetype-cli app {what} <provider-id|local> <model-id|file.bin>");
    }

    if (Process.GetProcessesByName("VoiceType").Length > 0)
    {
        return Fail("VoiceType.exe is running. Close it first (tray icon > exit), then run this command again.");
    }

    if (string.Equals(providerId, LocalModels.ProviderId, StringComparison.OrdinalIgnoreCase))
    {
        if (!LocalModels.Installed().Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return Fail($"local model '{model}' is not installed. Run: voicetype-cli local list");
        }
    }
    else
    {
        AddToApp(Open().Catalog.Get(providerId), model);
    }

    var app = AppSettings.Load();
    var choice = new EngineChoice { Provider = providerId.ToLowerInvariant(), Model = model };
    switch (what)
    {
        case "primary": app.Primary = choice; break;
        case "fallback": app.Fallback = choice; app.UseFallback = true; break;
        default: app.FilesEngine = choice; break;
    }

    app.Save();
    return Ok($"{what} = {choice.Key}");
}

string? ResolveKey(LabWorkspace ws, string providerId) =>
    ws.Keys.Get(providerId) ?? Environment.GetEnvironmentVariable("VOICETYPE_KEY_" + providerId.ToUpperInvariant().Replace('-', '_'));

int Fail(string message, int code = 1)
{
    Console.Error.WriteLine($"ERROR: {message}");
    return code;
}

int Paths()
{
    Console.WriteLine($"Settings folder     : {AppPaths.AppData}");
    Console.WriteLine($"Lab settings        : {LabSettings.FilePath}");
    Console.WriteLine($"Encrypted keys      : {AppPaths.KeysFile}");
    Console.WriteLine($"Custom providers    : {AppPaths.CustomProviders}");
    Console.WriteLine($"Recordings (default): {AppPaths.LabAudio}");
    Console.WriteLine($"Results             : {AppPaths.LabResults}");
    return 0;
}

int ProvidersList()
{
    var ws = Open();
    var rows = ws.Catalog.All.Select(d => new
    {
        id = d.Id,
        name = d.Name,
        source = ws.Catalog.GetSource(d.Id) == "built-in" ? "built-in" : "custom",
        pricing = d.Pricing ?? "unknown",
        needsKey = LabWorkspace.NeedsKey(d),
        hasKey = ws.HasKey(d.Id),
        keyUrl = d.KeyUrl,
        models = d.Models.Select(m => m.Id).ToList(),
        defaultModel = d.DefaultModel,
    }).ToList();

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(rows, json));
    }
    else
    {
        foreach (var r in rows)
        {
            var key = !r.needsKey ? "no key needed" : r.hasKey ? "key: saved" : "key: MISSING";
            Console.WriteLine($"{r.id,-20} {r.name,-28} [{r.source}] [{r.pricing}] {key}");
            foreach (var model in r.models)
            {
                Console.WriteLine($"    {(model == r.defaultModel ? "*" : " ")} {model}");
            }
        }
    }

    foreach (var error in ws.Catalog.LoadErrors)
    {
        Console.Error.WriteLine($"WARNING invalid custom provider file: {error}");
    }

    return 0;
}

int ProvidersShow(string id)
{
    if (id.Length == 0)
    {
        return Fail("usage: voicetype-cli providers show <provider-id>");
    }

    Console.WriteLine(ProviderDefinitionSerializer.Serialize(Open().Catalog.Get(id)));
    return 0;
}

int ProvidersAdd(string path)
{
    if (path.Length == 0 || !File.Exists(path))
    {
        return Fail("usage: voicetype-cli providers add <path-to-definition.json>   (see docs/custom-provider.md)");
    }

    var ws = Open();
    var definition = ws.AddCustomProvider(path);
    Console.WriteLine($"OK: provider '{definition.Id}' added ({definition.Models.Count} model(s) added to comparison).");
    if (LabWorkspace.NeedsKey(definition) && !ws.HasKey(definition.Id))
    {
        Console.WriteLine($"NEXT: voicetype-cli keys set {definition.Id} <api-key>");
    }

    return 0;
}

int ProvidersRemove(string id)
{
    if (id.Length == 0)
    {
        return Fail("usage: voicetype-cli providers remove <provider-id>");
    }

    return Open().RemoveCustomProvider(id)
        ? Ok($"provider '{id}' removed.")
        : Fail($"'{id}' is not a custom provider (built-in providers cannot be removed).");
}

int KeysList()
{
    var ws = Open();
    var rows = ws.Catalog.All.Where(LabWorkspace.NeedsKey).Select(d => new
    {
        provider = d.Id,
        name = d.Name,
        saved = ws.HasKey(d.Id),
        masked = Mask(ws.Keys.Get(d.Id)),
        keyUrl = d.KeyUrl,
    }).ToList();

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(rows, json));
        return 0;
    }

    foreach (var r in rows)
    {
        Console.WriteLine($"{r.provider,-20} {(r.saved ? "saved   " + r.masked : "missing ")}   {r.keyUrl}");
    }

    if (ws.Keys.Unreadable.Count > 0)
    {
        Console.WriteLine($"NOTE: keys for [{string.Join(", ", ws.Keys.Unreadable)}] were saved on another computer/user and must be entered again.");
    }

    return 0;
}

async Task<int> KeysSet(string providerId, string key)
{
    if (providerId.Length == 0 || key.Length == 0)
    {
        return Fail("usage: voicetype-cli keys set <provider-id> <api-key> [--no-test]");
    }

    var ws = Open();
    var definition = ws.Catalog.Get(providerId);
    ws.SaveKey(definition.Id, key);
    Console.WriteLine($"OK: key saved (encrypted) for '{definition.Id}': {Mask(ws.Keys.Get(definition.Id))}");
    return args.Contains("--no-test") ? 0 : await KeysTest(definition.Id);
}

int KeysAdd(string providerId, string key)
{
    if (providerId.Length == 0 || key.Length == 0)
    {
        return Fail("usage: voicetype-cli keys add <provider-id> <api-key>   (extra key from another account; used automatically when the first one hits its limit)");
    }

    var ws = Open();
    var definition = ws.Catalog.Get(providerId);
    var slot = ws.Keys.Add(definition.Id, key);
    var count = ws.Keys.List(definition.Id).Count;
    Console.WriteLine($"OK: key saved (encrypted) in slot '{slot}'. '{definition.Id}' now has {count} key(s).");
    return 0;
}

async Task<int> KeysTest(string providerId)
{
    if (providerId.Length == 0)
    {
        return Fail("usage: voicetype-cli keys test <provider-id>");
    }

    var ws = Open();
    using var http = new HttpClient();
    var result = await ws.TestKeyAsync(providerId, http);
    var status = result.Status switch
    {
        ConnectionStatus.Ok => "KEY OK",
        ConnectionStatus.Warning => "KEY OK WITH WARNING",
        _ => "KEY FAILED",
    };
    Console.WriteLine($"{status}: {result.Message}" + (result.Details is null ? string.Empty : $"  [{result.Details}]"));
    return result.Status == ConnectionStatus.Failed ? 2 : 0;
}

int KeysRemove(string providerId)
{
    if (providerId.Length == 0)
    {
        return Fail("usage: voicetype-cli keys remove <provider-id>");
    }

    Open().SaveKey(providerId, null);
    return Ok($"key removed for '{providerId}'.");
}

int ModelsList()
{
    var ws = Open();
    var rows = ws.Settings.Models.Select(m => new
    {
        provider = m.Provider,
        model = m.Model,
        enabled = m.Enabled,
        ready = ws.IsReady(m),
        variables = m.Variables,
    }).ToList();

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(rows, json));
        return 0;
    }

    foreach (var r in rows)
    {
        var state = !r.enabled ? "disabled" : r.ready ? "ACTIVE" : "no key";
        Console.WriteLine($"{state,-9} {r.provider,-18} {r.model}");
    }

    Console.WriteLine($"{ws.ActiveTargets().Count} model(s) will be used by 'compare run'.");
    return 0;
}

int ModelsAdd(string providerId, string model)
{
    if (providerId.Length == 0)
    {
        return Fail("usage: voicetype-cli models add <provider-id> <model-id> [--var name=value]");
    }

    var ws = Open();
    var definition = ws.Catalog.Get(providerId);
    var added = ws.Settings.Add(definition.Id, model.Length == 0 ? null : model);
    var entry = ws.Settings.Find(definition.Id, model.Length == 0 ? null : model)!;
    foreach (var variable in GetOptions("--var"))
    {
        var parts = variable.Split('=', 2);
        if (parts.Length == 2)
        {
            entry.Variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            entry.Variables[parts[0].Trim()] = parts[1].Trim();
        }
    }

    ws.Settings.Save();
    AddToApp(definition, model);
    Console.WriteLine(added ? $"OK: model '{entry}' added." : $"OK: model '{entry}' already exists (enabled).");
    if (LabWorkspace.NeedsKey(definition) && !ws.HasKey(definition.Id))
    {
        Console.WriteLine($"NEXT: voicetype-cli keys set {definition.Id} <api-key>");
    }

    return 0;
}

int ModelsRemove(string providerId, string model)
{
    var ws = Open();
    if (!ws.Settings.Remove(providerId, model.Length == 0 ? null : model))
    {
        return Fail($"model '{providerId}:{model}' not found. Run: voicetype-cli models list");
    }

    ws.Settings.Save();
    var app = AppSettings.Load();
    if (app.ExtraModels.RemoveAll(m => m.Same(new EngineChoice { Provider = providerId, Model = model })) > 0)
    {
        app.Save();
    }

    return Ok($"model '{providerId}:{model}' removed.");
}

int ModelsEnable(string providerId, string model, bool enabled)
{
    var ws = Open();
    var matches = ws.Settings.Models
        .Where(m => string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase) &&
                    (model is "" or "all" || string.Equals(m.Model, model, StringComparison.Ordinal)))
        .ToList();
    if (matches.Count == 0)
    {
        return Fail($"no model matches '{providerId}:{model}'. Run: voicetype-cli models list");
    }

    matches.ForEach(m => m.Enabled = enabled);
    ws.Settings.Save();
    return Ok($"{matches.Count} model(s) {(enabled ? "enabled" : "disabled")}.");
}

int SettingsShow()
{
    var ws = Open();
    Console.WriteLine($"language    : {(string.IsNullOrWhiteSpace(ws.Settings.Language) ? "auto" : ws.Settings.Language)}");
    Console.WriteLine($"audio folder: {ws.Settings.ResolvedAudioFolder}");
    Console.WriteLine($"parallelism : {ws.Settings.Parallelism}");
    return 0;
}

int SettingsLanguage(string language)
{
    if (language.Length == 0)
    {
        return Fail("usage: voicetype-cli settings language <ar|en|auto>");
    }

    var ws = Open();
    ws.Settings.Language = language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : language.ToLowerInvariant();
    ws.Settings.Save();
    return Ok($"language set to {language}.");
}

int SettingsAudioFolder(string folder)
{
    if (folder.Length == 0)
    {
        return Fail("usage: voicetype-cli settings audio-folder <path|default>");
    }

    var ws = Open();
    ws.Settings.AudioFolder = folder.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(folder);
    ws.Settings.Save();
    return Ok($"audio folder: {ws.Settings.ResolvedAudioFolder}");
}

int SamplesList()
{
    var ws = Open();
    var folder = GetOption("--audio") ?? ws.Settings.ResolvedAudioFolder;
    var samples = ComparisonRunner.LoadSamples(folder);
    Console.WriteLine($"Folder: {folder}");
    foreach (var s in samples)
    {
        Console.WriteLine($"  {s.Name,-20} {(s.Reference is null ? "NO reference text" : "reference: " + s.Reference)}");
    }

    Console.WriteLine($"{samples.Count} recording(s), {samples.Count(s => s.Reference is not null)} with reference text.");
    return 0;
}

int ComparePreview()
{
    var ws = Open();
    var folder = GetOption("--audio") ?? ws.Settings.ResolvedAudioFolder;
    var sample = ComparisonRunner.LoadSamples(folder).FirstOrDefault();
    var audio = sample is null ? VoiceType.Core.Speech.SampleAudio.Silence() : VoiceType.Core.Speech.AudioPayload.FromFile(sample.Path);
    var runner = ws.CreateRunner(new HttpClient());

    foreach (var target in TargetsWithEnvironmentKeys(ws))
    {
        var (run, engine) = runner.Prepare(target);
        Console.WriteLine($"===== {run.Label} =====");
        if (engine is null)
        {
            Console.WriteLine($"SKIP: {run.SkipReason}");
            continue;
        }

        Console.WriteLine(engine is DeclarativeSpeechEngine http
            ? http.Preview(audio, ws.TranscriptionOptions).ToString()
            : "(local model on this computer — no internet request)");
        (engine as IDisposable)?.Dispose();
        Console.WriteLine();
    }

    return 0;
}

async Task<int> CompareRun()
{
    var ws = Open();
    var folder = GetOption("--audio") ?? ws.Settings.ResolvedAudioFolder;
    var samples = ComparisonRunner.LoadSamples(folder);
    if (samples.Count == 0)
    {
        return Fail($"no recordings found in {folder}. Record sentences in VoiceType Lab, or put audio files + same-name .txt files there.");
    }

    var targets = TargetsWithEnvironmentKeys(ws);
    if (targets.Count == 0)
    {
        return Fail("no active models. Save at least one key: voicetype-cli keys set <provider-id> <api-key>");
    }

    Console.WriteLine($"Recordings: {samples.Count} ({samples.Count(s => s.Reference is not null)} with reference) — Models: {targets.Count}");

    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var runner = ws.CreateRunner(http);
    var gate = new object();
    var progress = new Progress<CompareProgress>(p =>
    {
        lock (gate)
        {
            Console.WriteLine($"[{p.Completed}/{p.Total}] {(p.Success ? "OK  " : "FAIL")} {p.Label} | {p.FileName} | {p.Message}");
        }
    });

    var results = await runner.RunAsync(samples, targets, ws.TranscriptionOptions, ws.Settings.Parallelism, progress, cts.Token);
    var output = ws.NewResultsFolder();
    ReportWriter.Write(output, results, ws.Settings.Language);

    Console.WriteLine();
    Console.WriteLine("Ranking (best first):");
    var rank = 1;
    foreach (var s in ReportWriter.Summarize(results).Where(s => s.SkipReason is null))
    {
        var wer = s.AverageWer is { } w ? $"{w:P1}" : "-";
        var latency = s.AverageLatency is { } l ? $"{l.TotalSeconds:0.00}s" : "-";
        Console.WriteLine($"  {rank++,2}. {s.Label,-50} WER {wer,7}  avg {latency,6}  ok {s.Succeeded} fail {s.Failed}");
    }

    var report = Path.Combine(output, "report.html");
    Console.WriteLine();
    Console.WriteLine($"Report: {report}");
    if (args.Contains("--open"))
    {
        Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
    }

    return 0;
}

IReadOnlyList<CompareTarget> TargetsWithEnvironmentKeys(LabWorkspace ws)
{
    // المفاتيح المحفوظة أولًا، ثم متغيرات البيئة VOICETYPE_KEY_<PROVIDER> (مفيدة للوكيل وللاختبار)
    var targets = new List<CompareTarget>();
    foreach (var model in ws.Settings.Models.Where(m => m.Enabled))
    {
        if (LabWorkspace.IsLocal(model.Provider))
        {
            if (ws.IsReady(model))
            {
                targets.Add(new CompareTarget(model.Provider, model.Model, null, ws.ModelLabel(model)));
            }

            continue;
        }

        if (!ws.Catalog.TryGet(model.Provider, out var definition))
        {
            continue;
        }

        var key = ResolveKey(ws, model.Provider);
        var ready = LabWorkspace.NeedsKey(definition) ? key is not null : ws.IsReady(model);
        if (ready)
        {
            targets.Add(new CompareTarget(model.Provider, model.Model, key, $"{definition.Name} · {model.Model ?? "default"}", model.Variables));
        }
    }

    return targets;
}

int LocalList()
{
    var installed = LocalModels.Installed();
    Console.WriteLine($"Models folder: {LocalModels.Folder}");
    Console.WriteLine($"GPU: {GpuAcceleration.Describe()}  (vulkan driver: {(GpuAcceleration.HasVulkanDriver ? "yes" : "no")}, acceleration files: {(GpuAcceleration.IsInstalled ? "installed" : "not installed")})");
    Console.WriteLine();
    Console.WriteLine("Suggested models (download with: voicetype-cli local download <file-name>):");
    foreach (var m in LocalModels.Suggested)
    {
        Console.WriteLine($"  {(installed.Contains(m.FileName) ? "[installed]" : "[ ]        ")} {m.FileName,-32} {m.SizeMegabytes,6:0} MB  {m.DisplayName}{(m.Recommended ? "  (recommended)" : string.Empty)}");
    }

    var extra = installed.Where(f => LocalModels.Suggested.All(m => m.FileName != f)).ToList();
    if (extra.Count > 0)
    {
        Console.WriteLine("Other installed models:");
        extra.ForEach(f => Console.WriteLine($"  [installed] {f}"));
    }

    return 0;
}

async Task<int> LocalDownload(string fileNameOrUrl)
{
    if (fileNameOrUrl.Length == 0)
    {
        return Fail("usage: voicetype-cli local download <file-name from 'local list' | direct https URL to a whisper.cpp .bin file>");
    }

    string url, fileName;
    if (fileNameOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        url = fileNameOrUrl;
        fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("the URL must point to a whisper.cpp model file ending with .bin");
        }
    }
    else
    {
        var info = LocalModels.Suggested.FirstOrDefault(m => string.Equals(m.FileName, fileNameOrUrl, StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            return Fail($"unknown model '{fileNameOrUrl}'. Run: voicetype-cli local list");
        }

        (url, fileName) = (info.DownloadUrl, info.FileName);
    }

    Console.WriteLine($"Downloading {fileName} ...");
    var last = string.Empty;
    var progress = new Progress<DownloadProgress>(p =>
    {
        var text = p.ToString();
        if (text != last)
        {
            Console.Write($"\r  {text}      ");
            last = text;
        }
    });
    await Downloader.DownloadFileAsync(url, LocalModels.PathOf(fileName), progress);
    Console.WriteLine();
    Open().RegisterLocalModel(fileName);
    return Ok($"model '{fileName}' installed and added to comparison.");
}

int LocalAddFile(string path)
{
    if (path.Length == 0)
    {
        return Fail("usage: voicetype-cli local add-file <path-to-model.bin>");
    }

    var fileName = LocalModels.AddFromFile(path);
    Open().RegisterLocalModel(fileName);
    return Ok($"model '{fileName}' copied to {LocalModels.Folder} and added to comparison.");
}

int LocalRemove(string fileName)
{
    if (fileName.Length == 0)
    {
        return Fail("usage: voicetype-cli local remove <file-name>");
    }

    Open().UnregisterLocalModel(fileName);
    return LocalModels.Remove(fileName) ? Ok($"model '{fileName}' deleted.") : Fail($"model '{fileName}' is not installed.");
}

async Task<int> LocalGpu()
{
    if (!GpuAcceleration.IsSupportedPlatform)
    {
        return Fail("GPU acceleration is available on Windows x64 only.");
    }

    if (!GpuAcceleration.HasVulkanDriver)
    {
        Console.WriteLine("WARNING: vulkan-1.dll not found — update your graphics driver, otherwise the CPU will be used.");
    }

    Console.WriteLine("Downloading GPU acceleration (Vulkan, ~37 MB) ...");
    await GpuAcceleration.InstallAsync(new Progress<DownloadProgress>(p => Console.Write($"\r  {p}      ")));
    Console.WriteLine();
    return Ok($"GPU acceleration installed in {GpuAcceleration.TargetDirectory}. Restart VoiceType Lab to use it.");
}

IEnumerable<string> GetOptions(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            yield return args[i + 1];
        }
    }
}

string? GetOption(string name) => GetOptions(name).FirstOrDefault();

static string Mask(string? key) =>
    key is null ? "-" : key.Length <= 8 ? new string('•', key.Length) : $"{key[..4]}••••{key[^4..]}";

static int Ok(string message)
{
    Console.WriteLine($"OK: {message}");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        voicetype-cli — VoiceType Lab command line (shares keys and settings with VoiceType Lab.exe)

        PROVIDERS
          voicetype-cli providers list [--json]          List providers, pricing (free / free-tier / trial / paid), key status, models
          voicetype-cli providers show <id>              Print a provider definition (JSON)
          voicetype-cli providers add <file.json>        Add a custom provider (see docs/custom-provider.md)
          voicetype-cli providers remove <id>            Remove a custom provider

        API KEYS (stored encrypted with Windows DPAPI)
          voicetype-cli keys list [--json]               Show which providers have keys (masked)
          voicetype-cli keys set <provider> <key>        Save a key, then test it (--no-test to skip)
          voicetype-cli keys add <provider> <key>        Add another key (another account) for automatic rotation
          voicetype-cli keys test <provider>             Test a saved key (exit code 2 = failed)
          voicetype-cli keys remove <provider>           Delete a key

        MODELS USED IN COMPARISON
          voicetype-cli models list [--json]             List models (ACTIVE / disabled / no key)
          voicetype-cli models add <provider> <model>    Add a model  (--var base_url=http://... for custom servers)
          voicetype-cli models remove <provider> <model>
          voicetype-cli models enable <provider> <model|all>
          voicetype-cli models disable <provider> <model|all>

        SETTINGS
          voicetype-cli settings show
          voicetype-cli settings language <ar|en|auto>
          voicetype-cli settings audio-folder <path|default>
          voicetype-cli app show                     Main program (VoiceType.exe): current primary / fallback / files models
          voicetype-cli app primary <provider> <model>   Set the dictation model (close VoiceType.exe first). Local: app primary local <file.bin>
          voicetype-cli app fallback <provider> <model>  Set the automatic fallback model
          voicetype-cli app files <provider> <model>     Set the model for audio-file transcription
          voicetype-cli paths                            Where files are stored

        MODELS ON THIS COMPUTER (free, offline — Whisper)
          voicetype-cli local list                       Suggested and installed local models, GPU status
          voicetype-cli local download <file|https-url>  Download a model (e.g. ggml-large-v3-turbo-q5_0.bin)
          voicetype-cli local add-file <model.bin>       Add a whisper.cpp model file you already have
          voicetype-cli local remove <file>              Delete a local model
          voicetype-cli local gpu                        Download GPU acceleration (Vulkan: NVIDIA / AMD / Intel)

        COMPARISON
          voicetype-cli samples list [--audio <folder>]  List recordings and their reference texts
          voicetype-cli compare preview                  Show HTTP requests without sending (keys hidden)
          voicetype-cli compare run [--audio <folder>] [--open]
                                                     Transcribe all recordings with all ACTIVE models,
                                                     write report.html / report.md / results.csv

        Keys can also come from environment variables VOICETYPE_KEY_<PROVIDER> (e.g. VOICETYPE_KEY_OPENROUTER).
        """);
    return 0;
}
