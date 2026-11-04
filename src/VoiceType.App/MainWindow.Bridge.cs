using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using VoiceType.App.Services;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Resilience;
using VoiceType.Providers.Rewrite;
using VoiceType.Settings;
using VoiceType.Speech;
using VoiceType.Text;

namespace VoiceType.App;

/// <summary>الطلبات التي ترسلها الواجهة للبرنامج.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _fileCts;

    // التلخيص والترجمة ليهم إلغاء لوحدهم: ميوقفوش تفريغ ملف شغال في الطابور
    private CancellationTokenSource? _textCts;
    private static readonly VoiceType.Speech.MeetingRecorder Meeting = new();
    private System.Windows.Threading.DispatcherTimer? _meetingTimer;
    private FileTranscript? _lastTranscript;

    private static AppServices S => App.Services;

    private async Task<object?> HandleAsync(string method, JsonObject p, IReadOnlyList<string> droppedFiles)
    {
        switch (method)
        {
            // ===== عام =====
            case "app.state":
                return State();
            case "app.openUrl":
                OpenUrl(Str(p, "url"));
                return null;
            case "app.openFolder":
                OpenFolder(Str(p, "kind") switch
                {
                    "models" => LocalModels.Folder,
                    "transcripts" => AppPaths.Transcripts,
                    "recordings" => AppPaths.LabAudio,
                    _ => AppPaths.Root,
                });
                return null;
            case "app.desktopShortcut":
                return new { path = StartupManager.CreateDesktopShortcut() };
            case "clipboard.copy":
                TextInjector.CopyToClipboard(Str(p, "text") ?? string.Empty);
                return null;

            // ===== الإعدادات =====
            case "settings.save":
                SaveSettings(p);
                return State();

            // ===== حسب البرنامج =====
            case "profiles.save":
                S.Settings.AppProfiles = System.Text.Json.JsonSerializer.Deserialize<List<AppProfile>>(
                    p["profiles"]?.ToJsonString() ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                S.Settings.Save();
                return State();
            case "profiles.reset":
                S.Settings.AppProfiles = null;
                S.Settings.Save();
                return State();

            // ===== تسجيل الاجتماعات =====
            case "meeting.start":
                StartMeeting(p["mic"]?.GetValue<bool>() ?? true);
                return new { recording = true };
            case "meeting.stop":
            {
                StopMeetingPreview();
                _meetingTimer?.Stop();
                var seconds = Meeting.Elapsed.TotalSeconds;
                var recorded = await Meeting.StopAsync();
                return new { path = recorded, name = Path.GetFileName(recorded), sizeMb = Math.Round(new FileInfo(recorded).Length / 1048576.0, 1), seconds = Math.Round(seconds) };
            }
            case "meeting.cancel":
                StopMeetingPreview();
                _meetingTimer?.Stop();
                Meeting.Cancel();
                return null;
            case "files.summarize":
                return await SummarizeAsync(Str(p, "text") ?? string.Empty);
            case "mic.setVisible":
                App.Current.SetMicVisible(p["visible"]?.GetValue<bool>() ?? true);
                return null;

            // ===== تجربة الإملاء داخل البرنامج =====
            case "dictation.testStart":
                App.Dictation.Start(DictationTarget.ReturnOnly);
                return null;
            case "dictation.testStop":
                await App.Dictation.StopAsync();
                return null;
            case "dictation.cancel":
                App.Dictation.Cancel();
                return null;

            // ===== النماذج على الجهاز =====
            case "local.list":
                return LocalList();
            case "local.download":
                return StartLocalDownload(Str(p, "file"), Str(p, "url"));
            case "local.cancel":
                if (Str(p, "key") is { } key && _downloads.TryGetValue(key, out var cts))
                {
                    cts.Cancel();
                }

                return null;
            case "local.remove":
                RemoveLocal(Str(p, "file") ?? string.Empty);
                return LocalList();
            case "local.addFile":
                return await AddLocalFromFileAsync();
            case "gpu.install":
                return StartGpuInstall();

            // ===== خدمات الإنترنت =====
            case "providers.list":
                return Providers();
            case "providers.saveKey":
            {
                var id = Required(p, "id");
                var keyValue = SecureKeyStore.Clean(Str(p, "key"))
                               ?? throw new InvalidOperationException("الصق المفتاح أولًا.");
                var slot = S.Keys.Add(id, keyValue);
                var result = await TestKeyAsync(id, keyValue);
                S.EnsureDefaults();
                return new { slot, status = result.Status.ToString(), message = result.Message, providers = Providers(), state = State() };
            }

            case "providers.testKey":
            {
                var id = Required(p, "id");
                var slot = Str(p, "slot");
                var keyValue = slot is null
                    ? S.Keys.Get(id)
                    : S.Keys.List(id).FirstOrDefault(k => k.Slot == slot)?.Value;
                var result = await TestKeyAsync(id, keyValue);
                return new { status = result.Status.ToString(), message = result.Message, providers = Providers() };
            }

            case "providers.removeKey":
            {
                var id = Required(p, "id");
                if (Str(p, "slot") is { } slot && S.Keys.List(id).Any(k => k.Slot == slot))
                {
                    S.Keys.RemoveSlot(slot);
                }
                else if (Str(p, "slot") is null)
                {
                    S.Keys.Remove(id);
                }

                S.EnsureDefaults();
                return new { providers = Providers(), state = State() };
            }

            // ===== التعلّم من Gemini =====
            case "learning.get":
                return Learning();
            case "learning.removeRule":
                S.Learned.RemoveRule(Required(p, "from"), Required(p, "to"));
                return Learning();
            case "learning.removeTerm":
                S.Learned.RemoveTerm(Required(p, "word"));
                return Learning();
            case "learning.clear":
                S.Learned.Clear();
                return Learning();

            // ===== سجل الجمل =====
            case "history.remove":
                App.Dictation.RemoveHistory(p["id"]?.GetValue<int>() ?? -1);
                return App.Dictation.History;
            case "history.clear":
                App.Dictation.ClearHistory();
                return App.Dictation.History;
            case "providers.fetchModels":
            {
                var id = Required(p, "id");
                return await ModelLister.ListAsync(S.Catalog.Get(id), S.Keys.Get(id), S.Http);
            }

            case "providers.addModel":
            {
                var choice = new EngineChoice { Provider = Required(p, "id"), Model = Required(p, "model").Trim() };
                if (!S.Settings.ExtraModels.Any(m => m.Same(choice)) &&
                    !S.Catalog.Get(choice.Provider).Models.Any(m => m.Id == choice.Model))
                {
                    S.Settings.ExtraModels.Add(choice);
                    S.Settings.Save();
                }

                return new { providers = Providers(), state = State() };
            }

            case "providers.removeModel":
                S.Settings.ExtraModels.RemoveAll(m => m.Same(new EngineChoice { Provider = Required(p, "id"), Model = Str(p, "model") }));
                S.Settings.Save();
                S.EnsureDefaults();
                return new { providers = Providers(), state = State() };

            // ===== تفريغ الملفات =====
            case "files.pick":
                return PickAudioFiles(p["multi"]?.GetValue<bool>() ?? false);
            case "files.dropped":
                return p["multi"]?.GetValue<bool>() == true
                    ? droppedFiles.Where(File.Exists).Select(FileInfoDto).ToList()
                    : droppedFiles.Count > 0 ? FileInfoDto(droppedFiles[0]) : null;
            case "files.transcribe":
                return await TranscribeFileAsync(Required(p, "path"), Str(p, "engine"), (int?)p["speakers"]);
            case "files.translate":
                return await TranslateAsync(Str(p, "text") ?? string.Empty);
            case "files.html":
                return new { html = TranscriptHtml(Str(p, "text") ?? string.Empty) };
            case "speakers.install":
                return StartSpeakersInstall();
            case "links.fetch":
                return await FetchLinkAsync(Required(p, "url"));

            // ===== الذكاء الاصطناعي للملخص والصياغة والترجمة =====
            case "textai.models":
                return await TextAiModelsAsync(Str(p, "provider") ?? "ollama", p["start"]?.GetValue<bool>() ?? true);
            case "textai.save":
                SaveTextAi(p);
                return State();
            case "textai.setKey":
                S.Keys.Set(AppServices.OllamaCloudKeyId, SecureKeyStore.Clean(Str(p, "key")));
                return State();
            case "textai.removeKey":
                S.Keys.Remove(AppServices.OllamaCloudKeyId);
                return State();
            case "textai.test":
                return await TestTextAiAsync();

            // ===== التحديثات من GitHub =====
            case "update.check":
                _update = await UpdateService.CheckAsync();
                return _update;
            case "update.install":
                return StartUpdateInstall();
            case "files.cancel":
                _fileCts?.Cancel();
                _textCts?.Cancel();
                return null;
            case "files.export":
                return await ExportTranscriptAsync(Required(p, "format"), Str(p, "text") ?? string.Empty, (int?)p["captionChars"]);
            case "files.showInFolder":
                if (Str(p, "path") is { } shownPath)
                {
                    OpenFolder(Path.GetDirectoryName(shownPath) ?? AppPaths.Transcripts, shownPath);
                }

                return null;

            default:
                throw new InvalidOperationException($"Unknown request: {method}");
        }
    }

    // ===== الحالة =====
    private static object State() => new
    {
        version = typeof(App).Assembly.GetName().Version?.ToString(3),
        dataFolder = AppPaths.Root,
        micVisible = App.Mic.IsVisible,
        hotkey = "Ctrl + Alt + Shift + M",
        dictationState = App.Dictation.State.ToString(),
        startWithWindows = SafeStartupState(),
        microphones = AudioRecorder.Devices(),
        history = App.Dictation.History,
        gpuInstalled = GpuAcceleration.IsInstalled,
        gpuSupported = GpuAcceleration.IsSupportedPlatform,
        usesLocal = new[] { S.Settings.Primary, S.Settings.UseFallback ? S.Settings.Fallback : null }
            .Any(c => c is not null && AppServices.IsLocal(c.Provider)),
        settings = new
        {
            language = S.Settings.Language ?? string.Empty,
            textMode = S.Settings.TextMode,
            lineBreaks = S.Settings.LineBreaks,
            learnFromOnline = S.Settings.LearnFromOnline,
            fixMixedDirection = S.Settings.FixMixedDirection,
            arabicStartsRight = S.Settings.ArabicStartsRight,
            liveTyping = S.Settings.LiveTyping,
            writingMode = S.Settings.WritingMode,
            useAppProfiles = S.Settings.UseAppProfiles,
            primary = S.Settings.Primary?.Key,
            fallback = S.Settings.Fallback?.Key,
            useFallback = S.Settings.UseFallback,
            filesEngine = S.Settings.FilesEngine?.Key,
            sounds = S.Settings.Sounds,
            microphoneDevice = S.Settings.MicrophoneDevice,
            theme = S.Settings.Theme,
            micSize = S.Settings.MicSize,
            micColor = S.Settings.MicColor,
            micAlwaysOnTop = S.Settings.MicAlwaysOnTop,
            micDimWhenIdle = S.Settings.MicDimWhenIdle,
            localBeamSize = S.Settings.LocalBeamSize,
            speakers = S.Settings.Speakers,
            captionChars = S.Settings.CaptionChars,
            detectMeetings = S.Settings.DetectMeetings,
            checkUpdates = S.Settings.CheckUpdates,
        },
        engines = S.Engines().Select(e => new { e.Key, e.Label, e.Kind, e.Pricing, e.Ready, e.Reason }),
        profiles = S.Settings.Profiles.Select(pr => new { pr.Id, pr.Name, pr.Match, pr.Style, pr.Writing, pr.Enabled }),
        recentApps = App.Dictation.RecentApps,
        meetingRecording = Meeting.IsRecording,
        hasGeminiKey = S.Keys.Has("gemini"),
        canRewrite = S.Keys.Has("gemini") || S.CreateTextEngine() is not null,
        speakersReady = SpeakerDiarizer.IsReady,
        linksReady = LinkDownloader.IsInstalled,
        textAi = new
        {
            provider = S.Settings.TextAiProvider,
            model = S.Settings.TextAiModel,
            ollamaUrl = S.Settings.OllamaUrl,
            lmStudioUrl = S.Settings.LmStudioUrl,
            hasOllamaCloudKey = S.Keys.Has(AppServices.OllamaCloudKeyId),
            ollamaInstalled = LocalTextServers.OllamaCli is not null,
            lmStudioInstalled = LocalTextServers.LmStudioCli is not null,
        },
    };

    private static bool SafeStartupState()
    {
        try
        {
            return StartupManager.IsEnabled();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void SaveSettings(JsonObject p)
    {
        var settings = S.Settings;
        if (p.ContainsKey("language"))
        {
            var language = Str(p, "language");
            settings.Language = string.IsNullOrWhiteSpace(language) ? null : language;
        }

        if (Str(p, "textMode") is { } mode)
        {
            settings.TextMode = mode;
        }

        if (Str(p, "lineBreaks") is { } lineBreaks)
        {
            settings.LineBreaks = lineBreaks;
        }

        if (p.ContainsKey("primary"))
        {
            settings.Primary = EngineChoice.Parse(Str(p, "primary"));
        }

        if (p.ContainsKey("fallback"))
        {
            settings.Fallback = EngineChoice.Parse(Str(p, "fallback"));
        }

        if (p.ContainsKey("filesEngine"))
        {
            settings.FilesEngine = EngineChoice.Parse(Str(p, "filesEngine"));
        }

        if (p["useFallback"] is JsonValue useFallback)
        {
            settings.UseFallback = useFallback.GetValue<bool>();
        }

        if (p["localBeamSize"] is JsonValue beam && int.TryParse(beam.ToString(), out var beamSize))
        {
            settings.LocalBeamSize = Math.Clamp(beamSize, 0, 8);
            S.ApplyLocalPrecision();
        }

        if (p["speakers"] is JsonValue speakers && int.TryParse(speakers.ToString(), out var speakerCount))
        {
            settings.Speakers = Math.Clamp(speakerCount, -1, 10);
        }

        if (p["captionChars"] is JsonValue caption && int.TryParse(caption.ToString(), out var chars))
        {
            settings.CaptionChars = Math.Clamp(chars, 0, 120);
        }

        if (p["detectMeetings"] is JsonValue detect)
        {
            settings.DetectMeetings = detect.GetValue<bool>();
            App.Current.SetMeetingDetection(settings.DetectMeetings);
        }

        if (p["checkUpdates"] is JsonValue updates)
        {
            settings.CheckUpdates = updates.GetValue<bool>();
        }

        if (p["fixMixedDirection"] is JsonValue direction)
        {
            settings.FixMixedDirection = direction.GetValue<bool>();
            TextInjector.FixMixedDirection = settings.FixMixedDirection;
        }

        if (Str(p, "writingMode") is { } writingMode)
        {
            settings.WritingMode = writingMode;
        }

        if (p["useAppProfiles"] is JsonValue useProfiles)
        {
            settings.UseAppProfiles = useProfiles.GetValue<bool>();
        }

        if (p["liveTyping"] is JsonValue liveTyping)
        {
            settings.LiveTyping = liveTyping.GetValue<bool>();
            if (settings.LiveTyping)
            {
                VoiceType.Providers.Resilience.EngineHealth.Shared.Success(AppServices.LiveEngineId);
            }
        }

        if (p["arabicStartsRight"] is JsonValue startRight)
        {
            settings.ArabicStartsRight = startRight.GetValue<bool>();
            TextInjector.ArabicStartsRight = settings.ArabicStartsRight;
        }

        if (p["learnFromOnline"] is JsonValue learn)
        {
            settings.LearnFromOnline = learn.GetValue<bool>();
        }

        if (p["sounds"] is JsonValue sounds)
        {
            settings.Sounds = sounds.GetValue<bool>();
            Sounds.Enabled = settings.Sounds;
        }

        if (p["microphoneDevice"] is JsonValue device)
        {
            settings.MicrophoneDevice = device.GetValue<int>();
        }

        var micChanged = false;
        if (Str(p, "theme") is { } theme && theme is "spotlight" or "orb" or "monolith")
        {
            // لون زر المايك العائم يتبع المظهر (ويمكن تغييره بعدها)
            if (!string.Equals(settings.Theme, theme, StringComparison.OrdinalIgnoreCase))
            {
                settings.MicColor = theme switch { "orb" => "teal", "monolith" => "copper", _ => "gold" };
                micChanged = true;
            }

            settings.Theme = theme;
        }

        if (Str(p, "micSize") is { } micSize)
        {
            settings.MicSize = micSize;
            micChanged = true;
        }

        if (Str(p, "micColor") is { } micColor)
        {
            settings.MicColor = micColor;
            micChanged = true;
        }

        if (p["micAlwaysOnTop"] is JsonValue onTop)
        {
            settings.MicAlwaysOnTop = onTop.GetValue<bool>();
            micChanged = true;
        }

        if (p["micDimWhenIdle"] is JsonValue dim)
        {
            settings.MicDimWhenIdle = dim.GetValue<bool>();
            micChanged = true;
        }

        if (micChanged)
        {
            App.Current.ApplyMicLook();
        }

        if (p["startWithWindows"] is JsonValue startup)
        {
            settings.StartWithWindows = startup.GetValue<bool>();
            StartupManager.Set(settings.StartWithWindows);
        }

        settings.Save();
    }

    // ===== النماذج المحلية =====
    private static object LocalList()
    {
        var installed = LocalModels.Installed();
        var suggested = LocalModels.Suggested.Select(m => new
        {
            file = m.FileName,
            name = m.DisplayName,
            sizeMb = m.SizeMegabytes,
            description = m.Description,
            recommended = m.Recommended,
            installed = installed.Contains(m.FileName, StringComparer.OrdinalIgnoreCase) && LocalModels.IsRunnable(m.FileName),
            shared = LocalModels.IsShared(m.FileName),
        });
        var extra = installed
            .Where(f => LocalModels.Suggested.All(m => !string.Equals(m.FileName, f, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new
            {
                file = f,
                name = LocalModels.DisplayNameOf(f),
                sizeMb = Math.Round(new FileInfo(LocalModels.PathOf(f)).Length / 1048576.0),
                description = LocalModels.IsShared(f) ? "من المجلد المشترك مع Vibe." : "نموذج أضفته بنفسك.",
                recommended = false,
                installed = true,
                shared = LocalModels.IsShared(f),
            });

        return new
        {
            models = suggested.Concat(extra),
            folder = LocalModels.Folder,
            gpu = new
            {
                supported = GpuAcceleration.IsSupportedPlatform,
                installed = GpuAcceleration.IsInstalled,
                driver = GpuAcceleration.HasVulkanDriver,
                status = GpuAcceleration.Describe(),
            },
        };
    }

    private object StartLocalDownload(string? file, string? url)
    {
        string downloadUrl;
        string fileName;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.AbsolutePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("الصق رابطًا مباشرًا يبدأ بـ https وينتهي بـ .bin (نموذج بصيغة whisper.cpp).");
            }

            downloadUrl = uri.ToString().Replace("/blob/", "/resolve/", StringComparison.Ordinal);
            fileName = Path.GetFileName(uri.AbsolutePath);
        }
        else
        {
            var info = LocalModels.Suggested.FirstOrDefault(m => string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("نموذج غير معروف.");
            downloadUrl = info.DownloadUrl;
            fileName = info.FileName;
        }

        var key = fileName;
        if (_downloads.ContainsKey(key))
        {
            return new { key };
        }

        var cts = new CancellationTokenSource();
        _downloads[key] = cts;
        _ = RunDownloadAsync(key, cts, async progress =>
            {
                // نماذج CrispASR (Cohere) محتاجة المحرك بتاعها: بينزل مرة واحدة قبل النموذج
                if (LocalModels.IsCrispModel(fileName) && !CrispRuntime.IsInstalled)
                {
                    await CrispRuntime.InstallAsync(progress, cts.Token);
                }

                if (!LocalModels.IsInstalled(fileName))
                {
                    await Downloader.DownloadFileAsync(downloadUrl, Path.Combine(LocalModels.Folder, fileName), progress, cts.Token);
                }
            },
            onDone: () => S.EnsureDefaults());
        return new { key };
    }

    private object StartGpuInstall()
    {
        const string key = "gpu";
        if (!_downloads.ContainsKey(key))
        {
            var cts = new CancellationTokenSource();
            _downloads[key] = cts;
            _ = RunDownloadAsync(key, cts, progress => GpuAcceleration.InstallAsync(progress, cts.Token), onDone: null);
        }

        return new { key };
    }

    private async Task RunDownloadAsync(string key, CancellationTokenSource cts, Func<IProgress<DownloadProgress>, Task> work, Action? onDone)
    {
        var progress = new Progress<DownloadProgress>(p => Push("download.progress", new { key, fraction = p.Fraction, text = p.ToString() }));
        try
        {
            await work(progress);
            onDone?.Invoke();
            Push("download.done", new { key, state = State() });
        }
        catch (OperationCanceledException)
        {
            Push("download.error", new { key, message = "أُلغي التنزيل." });
        }
        catch (HttpRequestException)
        {
            Push("download.error", new { key, message = "تعذّر التنزيل. تأكد من الاتصال بالإنترنت وحاول مرة أخرى." });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // أي خطأ تاني (ملف مضغوط بايظ، بصمة مختلفة، تشغيل فشل…): الواجهة لازم تعرف، وإلا شريط التقدم يفضل معلّق
            Push("download.error", new { key, message = string.IsNullOrWhiteSpace(ex.Message) ? "تعذّر التنزيل. حاول مرة أخرى." : ex.Message });
        }
        finally
        {
            _downloads.Remove(key);
            cts.Dispose();
        }
    }

    private static void RemoveLocal(string file)
    {
        if (LocalModels.IsShared(file))
        {
            // المجلد المشترك يستخدمه Vibe أيضًا: الحذف من صدى كان سيكسر Vibe
            throw new InvalidOperationException("النموذج ده في المجلد المشترك مع Vibe، فمش هيتمسح من صدى. لو عاوز تمسحه، امسحه من Vibe.");
        }

        S.ReleaseLocalModel(file);
        if (S.Settings.Primary?.Model == file || S.Settings.Fallback?.Model == file)
        {
            if (S.Settings.Primary?.Model == file)
            {
                S.Settings.Primary = null;
            }

            if (S.Settings.Fallback?.Model == file)
            {
                S.Settings.Fallback = null;
            }

            S.Settings.Save();
        }

        LocalModels.Remove(file);
        S.EnsureDefaults();
    }

    private async Task<object?> AddLocalFromFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "اختر ملف نموذج Whisper (.bin)", Filter = "نموذج Whisper (*.bin)|*.bin" };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        var file = await Task.Run(() => LocalModels.AddFromFile(dialog.FileName));
        S.EnsureDefaults();
        return new { file };
    }

    // ===== الخدمات =====
    private static object Providers() => S.OnlineProviders.Select(d => new
    {
        id = d.Id,
        name = d.Name,
        description = d.Description,
        pricing = d.Pricing ?? "unknown",
        pricingLabel = d.PricingLabel,
        pricingNote = d.PricingNote,
        keyUrl = d.KeyUrl ?? d.Website,
        needsKey = AppServices.NeedsKey(d),
        hasKey = S.Keys.Has(d.Id),
        keyFromName = d.KeyFrom is { } owner && S.Catalog.All.FirstOrDefault(o => string.Equals(o.Id, owner, StringComparison.OrdinalIgnoreCase)) is { } ownerDefinition ? ownerDefinition.Name : null,
        masked = Mask(S.Keys.Get(d.Id)),
        keys = S.Keys.List(d.Id).Select(k => new { slot = k.Slot, masked = Mask(k.Value), status = KeyStatusText(d.Id, k.Value) }),
        canListModels = ModelLister.Supports(d),
        models = d.Models.Select(m => new { id = m.Id, builtIn = true })
            .Concat(S.Settings.ExtraModels.Where(m => string.Equals(m.Provider, d.Id, StringComparison.OrdinalIgnoreCase))
                .Select(m => new { id = m.Model ?? string.Empty, builtIn = false })),
    }).ToList();

    private static object Learning() => new
    {
        enabled = S.Settings.LearnFromOnline,
        localModel = S.LearningLocalModel() is { } choice ? S.Describe(choice)?.Label : null,
        activeCount = S.Learned.ActiveRuleCount,
        minCount = LearnedCorrections.MinCountToApply,
        rules = S.Learned.Rules.Select(r => new { from = r.From, to = r.To, count = r.Count, active = r.Count >= LearnedCorrections.MinCountToApply }),
        terms = S.Learned.Terms.Take(60).Select(t => new { word = t.Word, count = t.Count }),
        termCount = S.Learned.Terms.Count,
    };

    /// <summary>يختبر مفتاحًا، ويعيده للخدمة إن نجح (لو كان متوقفًا مؤقتًا).</summary>
    private static async Task<ConnectionTestResult> TestKeyAsync(string providerId, string? key)
    {
        var result = await ConnectionTester.TestAsync(S.Catalog.Get(providerId), key, S.Http);
        if (result.Status == ConnectionStatus.Ok && key is not null)
        {
            KeyHealth.Shared.Reset(KeyHealth.Fingerprint(ResolveKeyOwner(providerId), key));
            KeyHealth.Shared.Reset(KeyHealth.Fingerprint(providerId, key));
            EngineHealth.Shared.ResetProvider(providerId);
        }

        return result;
    }

    private static string ResolveKeyOwner(string providerId) =>
        S.Catalog.All.FirstOrDefault(d => string.Equals(d.Id, providerId, StringComparison.OrdinalIgnoreCase))?.KeyFrom ?? providerId;

    /// <summary>حالة المفتاح للعرض: يعمل، أو متوقف مؤقتًا لأنه تجاوز حده أو رُفض.</summary>
    private static string? KeyStatusText(string providerId, string key)
    {
        var status = KeyHealth.Shared.Get(KeyHealth.Fingerprint(providerId, key));
        if (status.PausedUntil is { } until && until > KeyHealth.Shared.Now)
        {
            var reason = status.LastError switch
            {
                SpeechErrorKind.RateLimited => "تجاوز الحد المجاني",
                SpeechErrorKind.PaymentRequired => "يطلب الدفع",
                SpeechErrorKind.Unauthorized or SpeechErrorKind.Forbidden => "مرفوض",
                _ => "متوقف",
            };
            return $"{reason} — يُتخطى حتى {until.ToLocalTime():HH:mm}";
        }

        return status.LastSuccess is not null ? "يعمل" : null;
    }

    private static string? Mask(string? key) =>
        key is null ? null : key.Length <= 8 ? new string('•', key.Length) : $"{key[..4]}••••{key[^4..]}";

    // ===== تفريغ الملفات =====
    private object? PickAudioFiles(bool multi)
    {
        var extensions = string.Join(";", AudioDecoder.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = multi ? "اختار ملف أو أكتر" : "اختر ملفًا صوتيًا",
            Filter = $"ملفات صوت وفيديو ({extensions})|{extensions}|كل الملفات (*.*)|*.*",
            Multiselect = multi,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        return multi ? dialog.FileNames.Select(FileInfoDto).ToList() : FileInfoDto(dialog.FileName);
    }

    private static object FileInfoDto(string path) => new
    {
        path,
        name = Path.GetFileName(path),
        sizeMb = Math.Round(new FileInfo(path).Length / 1048576.0, 1),
    };

    // تفريغ ملف أو تنزيل لينك شغال دلوقتي (عشان التحديث ميقفلش البرنامج في النص)
    private bool _fileBusy;

    private async Task<object> TranscribeFileAsync(string path, string? engineKey, int? speakers)
    {
        _fileBusy = true;
        try
        {
            return await TranscribeFileCoreAsync(path, engineKey, speakers);
        }
        finally
        {
            _fileBusy = false;
        }
    }

    private async Task<object> TranscribeFileCoreAsync(string path, string? engineKey, int? speakers)
    {
        var choice = EngineChoice.Parse(engineKey) ?? S.Settings.FilesEngine ?? S.Settings.Primary
                     ?? throw new InvalidOperationException("اختر نموذجًا أولًا.");
        if (!S.IsReady(choice))
        {
            throw new InvalidOperationException("هذا النموذج غير جاهز. نزّله أو أضف مفتاح الخدمة من صفحة النماذج.");
        }

        S.Settings.FilesEngine = choice;
        S.Settings.Save();

        _fileCts?.Cancel();
        _fileCts = new CancellationTokenSource();
        var engine = S.CreateEngine(choice);
        var progress = new Progress<FileTranscriptionProgress>(p => Push("file.progress", new { fraction = p.Fraction, stage = p.Stage }));

        // «مين اللي بيتكلم»: -1 = مقفول، 0 = تلقائي، أو عدد المتكلمين
        var who = speakers ?? S.Settings.Speakers;
        if (speakers is { } chosen && chosen != S.Settings.Speakers)
        {
            S.Settings.Speakers = Math.Clamp(chosen, -1, 10);
            S.Settings.Save();
        }

        var labelSpeakers = who >= 0 && AppServices.IsLocal(choice.Provider);
        if (labelSpeakers && !SpeakerDiarizer.IsReady)
        {
            throw new InvalidOperationException("معرفة المتكلمين محتاجة تنزّل نموذجها الأول (24 ميجا). دوس «تنزيل» جنب «مين اللي بيتكلم».");
        }

        var transcriber = new FileTranscriber
        {
            SpeakerLabeler = labelSpeakers
                ? async (samples, segments, ct) =>
                {
                    using var diarizer = new SpeakerDiarizer();
                    return await diarizer.LabelAsync(samples, segments, who, ct);
                }
                : null,
        };
        var transcript = await transcriber.TranscribeAsync(path, engine, S.TranscriptionOptions, progress, _fileCts.Token);
        _lastTranscript = transcript;

        // نسخة تلقائية في مجلد النصوص
        if (AppServices.IsLocal(choice.Provider))
        {
            // ما تعلّمه النموذج المحلي من Gemini يُطبَّق على الملفات أيضًا (على الأجزاء كمان، عشان SRT والتصدير يطابقوا النص)
            transcript = transcript with
            {
                Text = S.Learned.Apply(transcript.Text),
                Segments = transcript.Segments.Select(segment => segment with { Text = S.Learned.Apply(segment.Text) }).ToList(),
            };
            _lastTranscript = transcript;
        }

        var autoSave = UniquePath(Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), Path.GetFileNameWithoutExtension(path) + ".txt"));
        TranscriptExporter.SaveText(autoSave, transcript.Text);

        return new
        {
            text = transcript.Text,
            segments = transcript.Segments.Count,
            speakers = transcript.SpeakerCount,
            speakersSkipped = who >= 0 && !labelSpeakers,
            hasTimestamps = engine is ISegmentedSpeechEngine,
            durationSeconds = Math.Round(transcript.AudioDuration.TotalSeconds),
            elapsedSeconds = Math.Round(transcript.Elapsed.TotalSeconds, 1),
            engine = S.Describe(choice)?.Label ?? choice.Key,
            savedTo = autoSave,
        };
    }

    /// <summary>اسم ملف مش موجود قبل كده («اسم (2).txt»…)، عشان تفريغ ملف تاني بنفس الاسم ميمسحش القديم.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static UpdateService.UpdateInfo? _update;

    /// <summary>ينزّل التحديث ويركّبه، وبعدين صدى يقفل ويفتح بالنسخة الجديدة.</summary>
    private object StartUpdateInstall()
    {
        if (Meeting.IsRecording || App.Dictation.State != DictationState.Idle)
        {
            throw new InvalidOperationException("استنى لما الإملاء أو تسجيل الاجتماع يخلص، وبعدين حدّث.");
        }

        // البرنامج هيقفل بعد التحديث: أي تفريغ ملف أو تنزيل شغال كان هيضيع
        if (_fileBusy || _downloads.Keys.Any(k => k != "update"))
        {
            throw new InvalidOperationException("استنى لما تفريغ الملف أو التنزيل الشغال يخلص، وبعدين حدّث.");
        }

        var update = _update is { Available: true } ? _update : throw new InvalidOperationException("دوّر على تحديث الأول.");
        const string key = "update";
        if (!_downloads.ContainsKey(key))
        {
            var cts = new CancellationTokenSource();
            _downloads[key] = cts;
            _ = RunDownloadAsync(key, cts, progress => UpdateService.InstallAsync(update, progress, cts.Token), onDone: () =>
            {
                UpdateService.RestartAfterUpdate();
                Dispatcher.InvokeAsync(() => App.Current.ExitApp());
            });
        }

        return new { key };
    }

    /// <summary>هل فيه تسجيل اجتماع شغال؟</summary>
    internal static bool MeetingRecording => Meeting.IsRecording;

    /// <summary>يبدأ تسجيل الاجتماع (من الزرار، أو لما صدى يلاقي اجتماع بدأ).</summary>
    internal void StartMeeting(bool includeMic)
    {
        if (Meeting.IsRecording)
        {
            return;
        }

        Meeting.Start(Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), "تسجيلات"), includeMic);
        _meetingTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _meetingTimer.Tick -= OnMeetingTick;
        _meetingTimer.Tick += OnMeetingTick;
        _meetingTimer.Start();
        StartMeetingPreview();
    }

    private CancellationTokenSource? _previewCts;

    /// <summary>
    /// الكلام يظهر أثناء الاجتماع: كل 20 ثانية بناخد الصوت الجديد ونفرّغه بالنموذج اللي على الجهاز (Cohere سريع).
    /// ده للعرض بس؛ التفريغ النهائي بعد الإيقاف بيكون على الملف كله (أدق، ومعاه مين اللي بيتكلم).
    /// لو مفيش نموذج على الجهاز، مفيش عرض (مش بنبعت صوت الاجتماع للإنترنت كل شوية).
    /// </summary>
    private void StartMeetingPreview()
    {
        StopMeetingPreview();
        var choice = new[] { S.Settings.Fallback, S.Settings.Primary, S.Settings.FilesEngine }
            .FirstOrDefault(c => c is not null && AppServices.IsLocal(c.Provider) && S.IsReady(c));
        if (choice is null)
        {
            Push("meeting.live", new { text = (string?)null, note = "العرض أثناء الاجتماع محتاج نموذج على جهازك (Cohere أو Whisper)." });
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = Task.Run(async () =>
        {
            var engine = S.CreateEngine(choice);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);
                    var audio = Meeting.TakeNewAudio16k();
                    if (audio.Length < AudioConverter.SampleRate * 2 || Rms(audio) < 0.004)
                    {
                        continue; // سكوت
                    }

                    // من غير إلغاء: لو وقفنا التسجيل والجزء ده لسه بيتفرّغ، بنسيبه يخلص ونرمي نتيجته.
                    // الإلغاء كان بيقفل المساعد، فالتفريغ النهائي (والإملاء) يضطروا يحمّلوا النموذج من الأول.
                    var result = await engine.TranscribeAsync(new Core.Speech.AudioPayload(AudioDecoder.ToWav16k(audio), "wav", "live.wav"), S.TranscriptionOptions, CancellationToken.None);
                    if (!cts.IsCancellationRequested && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        Push("meeting.live", new { text = result.Text.Trim() });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // العرض اختياري: لو جزء فشل نكمّل، والتسجيل نفسه مش بيتأثر
                }
            }
        });
    }

    private void StopMeetingPreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    /// <summary>الاجتماع خلص: الواجهة توقف التسجيل وتفرّغه زي زرار «إيقاف وتفريغ».</summary>
    internal void RequestMeetingStop() => Push("meeting.stopRequested", null);

    private void OnMeetingTick(object? sender, EventArgs e) =>
        Push("meeting.tick", new { seconds = Math.Floor(Meeting.Elapsed.TotalSeconds), level = Math.Round(Meeting.Level, 3) });

    /// <summary>ملخص + نقاط + قرارات + مهام، ويُحفظ بجانب النص.</summary>
    private async Task<object> SummarizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("لا يوجد نص للتلخيص.");
        }

        var rewriter = S.CreateRewriter() ?? throw new InvalidOperationException("التلخيص محتاج موديل ذكاء اصطناعي: اختار واحد من الإعدادات (Ollama أو LM Studio)، أو أضف مفتاح Gemini (مجاني).");
        _textCts?.Cancel();
        _textCts = new CancellationTokenSource();
        var summary = await rewriter.SummarizeAsync(text, _textCts.Token);
        var source = _lastTranscript?.FilePath;
        var name = (source is null ? "تفريغ" : Path.GetFileNameWithoutExtension(source)) + " - ملخص.txt";
        var saved = Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), name);
        TranscriptExporter.SaveText(saved, summary);
        return new { summary, savedTo = saved, engine = rewriter.LastEngine };
    }

    /// <summary>ترجمة النص كله للإنجليزي، ويتحفظ جنب النص.</summary>
    private async Task<object> TranslateAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("مفيش نص للترجمة.");
        }

        var rewriter = S.CreateRewriter() ?? throw new InvalidOperationException("الترجمة محتاجة موديل ذكاء اصطناعي: اختار واحد من الإعدادات، أو أضف مفتاح Gemini (مجاني).");
        _textCts?.Cancel();
        _textCts = new CancellationTokenSource();
        var translation = await rewriter.TranslateAsync(text, _textCts.Token);
        var saved = Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), BaseName() + " - English.txt");
        TranscriptExporter.SaveText(saved, translation);
        return new { translation, savedTo = saved, engine = rewriter.LastEngine };
    }

    private string BaseName() => _lastTranscript?.FilePath is { } source ? Path.GetFileNameWithoutExtension(source) : "تفريغ";

    /// <summary>الأجزاء بأوقاتها لو النص لسه زي ما طلع (لو المستخدم عدّله، نستخدم النص المعدّل من غير أوقات).</summary>
    private IReadOnlyList<TranscriptSegment> SegmentsFor(string text) =>
        _lastTranscript is { } t && string.Equals(t.Text.Trim(), text.Trim(), StringComparison.Ordinal) ? t.Segments : [];

    private string TranscriptHtml(string text)
    {
        var subtitle = _lastTranscript is { } t
            ? $"{DateTime.Now:yyyy-MM-dd} · المدة {TimeSpan.FromSeconds(Math.Round(t.AudioDuration.TotalSeconds)):g}" + (t.HasSpeakers ? $" · {t.SpeakerCount} متكلمين" : string.Empty)
            : DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return TranscriptExporter.ToHtml(BaseName(), text, SegmentsFor(text), subtitle);
    }

    private async Task<object?> ExportTranscriptAsync(string format, string text, int? captionChars)
    {
        var baseName = BaseName();
        var (filter, extension) = format switch
        {
            "docx" => ("مستند Word (*.docx)|*.docx", ".docx"),
            "srt" => ("ترجمة SRT (*.srt)|*.srt", ".srt"),
            "vtt" => ("ترجمة WebVTT (*.vtt)|*.vtt", ".vtt"),
            "json" => ("JSON (*.json)|*.json", ".json"),
            "html" => ("صفحة HTML (*.html)|*.html", ".html"),
            "pdf" => ("PDF (*.pdf)|*.pdf", ".pdf"),
            _ => ("ملف نصي (*.txt)|*.txt", ".txt"),
        };

        var segments = SegmentsFor(text);
        if (format is "srt" or "vtt" && segments.Count == 0)
        {
            throw new InvalidOperationException(_lastTranscript is null
                ? "مفيش أوقات. فرّغ الملف الأول."
                : "النص اتعدّل بعد التفريغ، فالأوقات مبقتش مظبوطة. فرّغ الملف تاني لو عاوز ترجمة بأوقات.");
        }

        var chars = captionChars ?? S.Settings.CaptionChars;
        if (captionChars is { } chosenChars && chosenChars != S.Settings.CaptionChars)
        {
            S.Settings.CaptionChars = Math.Clamp(chosenChars, 0, 120);
            S.Settings.Save();
        }

        var dialog = new SaveFileDialog
        {
            Title = "حفظ التفريغ",
            Filter = filter,
            FileName = baseName + extension,
            InitialDirectory = AppPaths.Ensure(AppPaths.Transcripts),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        switch (format)
        {
            case "docx":
                TranscriptExporter.SaveDocx(dialog.FileName, baseName, text);
                break;
            case "srt":
                TranscriptExporter.SaveSrt(dialog.FileName, segments, chars);
                break;
            case "vtt":
                TranscriptExporter.SaveVtt(dialog.FileName, segments, chars);
                break;
            case "json":
                TranscriptExporter.SaveJson(dialog.FileName, baseName, text, segments, _lastTranscript?.EngineId, _lastTranscript?.AudioDuration);
                break;
            case "html":
                File.WriteAllText(dialog.FileName, TranscriptHtml(text), new System.Text.UTF8Encoding(false));
                break;
            case "pdf":
                await PrintHtmlToPdfAsync(TranscriptHtml(text), dialog.FileName);
                break;
            default:
                TranscriptExporter.SaveText(dialog.FileName, text);
                break;
        }

        return new { path = dialog.FileName };
    }

    /// <summary>
    /// PDF بالعربي مظبوط (اتجاه وحروف موصولة): بنفتح صفحة HTML في متصفح مخفي (WebView2 نفسه)
    /// ونطبعها PDF. من غير أي برنامج زيادة.
    /// </summary>
    private async Task PrintHtmlToPdfAsync(string html, string path)
    {
        var environment = Web.CoreWebView2?.Environment ?? throw new InvalidOperationException("الواجهة لسه بتحمّل. جرّب تاني بعد ثانية.");
        var temp = Path.Combine(Path.GetTempPath(), $"voicetype-print-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(temp, html, new System.Text.UTF8Encoding(false));
        var controller = await environment.CreateCoreWebView2ControllerAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            controller.IsVisible = false;
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            controller.CoreWebView2.Navigate(new Uri(temp).AbsoluteUri);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("تعذّر تجهيز الصفحة للطباعة.");
            }

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.MarginTop = settings.MarginBottom = 0.5;
            settings.MarginLeft = settings.MarginRight = 0.5;
            if (!await controller.CoreWebView2.PrintToPdfAsync(path, settings))
            {
                throw new InvalidOperationException("تعذّر حفظ الـ PDF.");
            }
        }
        finally
        {
            controller.Close();
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>تنزيل نموذج بصمة الأصوات (ومحرك CrispASR لو مش موجود).</summary>
    private object StartSpeakersInstall()
    {
        const string key = "speakers";
        if (!_downloads.ContainsKey(key))
        {
            var cts = new CancellationTokenSource();
            _downloads[key] = cts;
            _ = RunDownloadAsync(key, cts, progress => SpeakerDiarizer.InstallAsync(progress, cts.Token), onDone: null);
        }

        return new { key };
    }

    /// <summary>ينزّل صوت اللينك (وأداة التنزيل نفسها أول مرة)، ويرجّعه كملف جاهز للتفريغ.</summary>
    private async Task<object> FetchLinkAsync(string url)
    {
        _fileBusy = true;
        try
        {
            return await FetchLinkCoreAsync(url);
        }
        finally
        {
            _fileBusy = false;
        }
    }

    private async Task<object> FetchLinkCoreAsync(string url)
    {
        if (!LinkDownloader.IsValidLink(url))
        {
            throw new InvalidOperationException("ده مش لينك صحيح. الصق لينك يبدأ بـ https://");
        }

        _fileCts?.Cancel();
        _fileCts = new CancellationTokenSource();
        var token = _fileCts.Token;
        LinkDownloader.CleanupDownloads();
        if (LinkDownloader.NeedsRefresh)
        {
            await InstallLinkToolAsync(token);
        }

        var audioProgress = new Progress<double>(f =>
            Push("file.progress", new { fraction = f, stage = $"بنزّل الصوت من اللينك… {f:P0}" }));
        Push("file.progress", new { fraction = 0.0, stage = "بنزّل الصوت من اللينك…" });
        string file;
        try
        {
            file = await LinkDownloader.DownloadAudioAsync(url, audioProgress, token);
        }
        catch (InvalidOperationException) when (DateTime.UtcNow - File.GetLastWriteTimeUtc(LinkDownloader.ToolPath) > TimeSpan.FromDays(1))
        {
            // غالبًا الموقع غيّر طريقته: نجيب آخر نسخة من الأداة ونجرّب مرة كمان
            await InstallLinkToolAsync(token);
            file = await LinkDownloader.DownloadAudioAsync(url, audioProgress, token);
        }

        return FileInfoDto(file);
    }

    private async Task InstallLinkToolAsync(CancellationToken token)
    {
        Push("file.progress", new { fraction = 0.0, stage = "بنجيب آخر نسخة من أداة تنزيل اللينكات…" });
        await LinkDownloader.InstallAsync(new Progress<DownloadProgress>(d =>
            Push("file.progress", new { fraction = d.Fraction ?? 0, stage = $"بنجيب آخر نسخة من أداة تنزيل اللينكات… {d}" })), token);
    }

    /// <summary>الموديلات المتاحة على Ollama / LM Studio (وبنشغّل الخادم لو متسطّب ومقفول).</summary>
    private static async Task<object> TextAiModelsAsync(string provider, bool start)
    {
        if (S.TextServer(provider) is not { } server)
        {
            return new { models = Array.Empty<string>(), error = (string?)null };
        }

        if (provider == "ollama-cloud" && server.Key is null)
        {
            return new { models = Array.Empty<string>(), error = (string?)"أضف مفتاح Ollama Cloud الأول (من ollama.com ← Settings ← Keys)." };
        }

        try
        {
            return new { models = await TextServerEngine.ListModelsAsync(S.Http, server.Kind, server.Url, server.Key, CancellationToken.None), error = (string?)null };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or ArgumentException)
        {
            // LM Studio ممكن يكون شغال على بورت تاني (أو نشغّله ونعرف بورته): نحفظ العنوان الصح
            if (provider == "lmstudio" && IsLocalUrl(server.Url))
            {
                var running = await LocalTextServers.LmStudioRunningUrlAsync(CancellationToken.None);
                if (running is null && start && LocalTextServers.LmStudioCli is not null)
                {
                    await LocalTextServers.TryStartAsync(S.Http, server.Kind, server.Url, CancellationToken.None);
                    running = await LocalTextServers.LmStudioRunningUrlAsync(CancellationToken.None);
                }

                if (running is not null)
                {
                    try
                    {
                        var found = await TextServerEngine.ListModelsAsync(S.Http, server.Kind, running, null, CancellationToken.None);
                        S.Settings.LmStudioUrl = running;
                        S.Settings.Save();
                        return new { models = found, error = (string?)null, url = running };
                    }
                    catch (Exception again) when (again is HttpRequestException or InvalidOperationException or TaskCanceledException)
                    {
                        ex = again;
                    }
                }
            }

            if (start && provider != "lmstudio" && await LocalTextServers.TryStartAsync(S.Http, server.Kind, server.Url, CancellationToken.None))
            {
                try
                {
                    return new { models = await TextServerEngine.ListModelsAsync(S.Http, server.Kind, server.Url, server.Key, CancellationToken.None), error = (string?)null };
                }
                catch (Exception again) when (again is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    ex = again;
                }
            }

            var name = provider == "lmstudio" ? "LM Studio" : "Ollama";
            return new { models = Array.Empty<string>(), error = (string?)$"{name} مش بيرد على {server.Url}. اتأكد إنه متسطّب وشغال. ({ex.Message})" };
        }
    }

    private static bool IsLocalUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static void SaveTextAi(JsonObject p)
    {
        var settings = S.Settings;
        if (Str(p, "provider") is { } provider && provider is "gemini" or "ollama" or "ollama-cloud" or "lmstudio")
        {
            settings.TextAiProvider = provider;
        }

        if (p.ContainsKey("model"))
        {
            settings.TextAiModel = string.IsNullOrWhiteSpace(Str(p, "model")) ? null : Str(p, "model")!.Trim();
        }

        if (Str(p, "ollamaUrl") is { Length: > 0 } ollama)
        {
            settings.OllamaUrl = TextServerEngine.NormalizeUrl(ollama);
        }

        if (Str(p, "lmStudioUrl") is { Length: > 0 } lmStudio)
        {
            settings.LmStudioUrl = TextServerEngine.NormalizeUrl(lmStudio);
        }

        settings.Save();
    }

    private static async Task<object> TestTextAiAsync()
    {
        var rewriter = S.CreateRewriter() ?? throw new InvalidOperationException("اختار موديل أو أضف مفتاح Gemini الأول.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var text = await rewriter.RewriteAsync("ازيك يا صاحبي، عاوز اقولك ان الاجتماع بكره الساعه عشره الصبح", WritingMode.Formal);
        return new { text, engine = rewriter.LastEngine, seconds = Math.Round(started.Elapsed.TotalSeconds, 1), failures = rewriter.LastFailures };
    }

    // ===== أدوات =====
    private static string? Str(JsonObject p, string name) =>
        p[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Required(JsonObject p, string name) =>
        Str(p, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing '{name}'.");
}
