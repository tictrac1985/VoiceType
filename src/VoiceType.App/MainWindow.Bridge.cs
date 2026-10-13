using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using VoiceType.App.Services;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Resilience;
using VoiceType.Settings;
using VoiceType.Speech;
using VoiceType.Text;

namespace VoiceType.App;

/// <summary>الطلبات التي ترسلها الواجهة للبرنامج.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _fileCts;
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
                Meeting.Start(Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), "تسجيلات"), p["mic"]?.GetValue<bool>() ?? true);
                _meetingTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _meetingTimer.Tick -= OnMeetingTick;
                _meetingTimer.Tick += OnMeetingTick;
                _meetingTimer.Start();
                return new { recording = true };
            case "meeting.stop":
            {
                _meetingTimer?.Stop();
                var seconds = Meeting.Elapsed.TotalSeconds;
                var recorded = await Meeting.StopAsync();
                return new { path = recorded, name = Path.GetFileName(recorded), sizeMb = Math.Round(new FileInfo(recorded).Length / 1048576.0, 1), seconds = Math.Round(seconds) };
            }
            case "meeting.cancel":
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
                return PickAudioFile();
            case "files.dropped":
                return droppedFiles.Count > 0 ? FileInfoDto(droppedFiles[0]) : null;
            case "files.transcribe":
                return await TranscribeFileAsync(Required(p, "path"), Str(p, "engine"));
            case "files.cancel":
                _fileCts?.Cancel();
                return null;
            case "files.export":
                return ExportTranscript(Required(p, "format"), Str(p, "text") ?? string.Empty);
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
        },
        engines = S.Engines().Select(e => new { e.Key, e.Label, e.Kind, e.Pricing, e.Ready, e.Reason }),
        profiles = S.Settings.Profiles.Select(pr => new { pr.Id, pr.Name, pr.Match, pr.Style, pr.Writing, pr.Enabled }),
        recentApps = App.Dictation.RecentApps,
        meetingRecording = Meeting.IsRecording,
        hasGeminiKey = S.Keys.Has("gemini"),
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
            installed = installed.Contains(m.FileName, StringComparer.OrdinalIgnoreCase),
        });
        var extra = installed
            .Where(f => LocalModels.Suggested.All(m => !string.Equals(m.FileName, f, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new
            {
                file = f,
                name = LocalModels.DisplayNameOf(f),
                sizeMb = Math.Round(new FileInfo(LocalModels.PathOf(f)).Length / 1048576.0),
                description = "نموذج أضفته بنفسك.",
                recommended = false,
                installed = true,
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
        _ = RunDownloadAsync(key, cts, progress =>
            Downloader.DownloadFileAsync(downloadUrl, Path.Combine(LocalModels.Folder, fileName), progress, cts.Token),
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Push("download.error", new { key, message = "تعذّر التنزيل. تأكد من الاتصال بالإنترنت وحاول مرة أخرى." });
        }
        finally
        {
            _downloads.Remove(key);
            cts.Dispose();
        }
    }

    private static void RemoveLocal(string file)
    {
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
    private object? PickAudioFile()
    {
        var extensions = string.Join(";", AudioDecoder.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملفًا صوتيًا",
            Filter = $"ملفات صوتية ({extensions})|{extensions}|كل الملفات (*.*)|*.*",
        };
        return dialog.ShowDialog(this) == true ? FileInfoDto(dialog.FileName) : null;
    }

    private static object FileInfoDto(string path) => new
    {
        path,
        name = Path.GetFileName(path),
        sizeMb = Math.Round(new FileInfo(path).Length / 1048576.0, 1),
    };

    private async Task<object> TranscribeFileAsync(string path, string? engineKey)
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

        var transcript = await new FileTranscriber().TranscribeAsync(path, engine, S.TranscriptionOptions, progress, _fileCts.Token);
        _lastTranscript = transcript;

        // نسخة تلقائية في مجلد النصوص
        if (AppServices.IsLocal(choice.Provider))
        {
            // ما تعلّمه النموذج المحلي من Gemini يُطبَّق على الملفات أيضًا
            transcript = transcript with { Text = S.Learned.Apply(transcript.Text) };
            _lastTranscript = transcript;
        }

        var autoSave = Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), Path.GetFileNameWithoutExtension(path) + ".txt");
        TranscriptExporter.SaveText(autoSave, transcript.Text);

        return new
        {
            text = transcript.Text,
            segments = transcript.Segments.Count,
            hasTimestamps = engine is ISegmentedSpeechEngine,
            durationSeconds = Math.Round(transcript.AudioDuration.TotalSeconds),
            elapsedSeconds = Math.Round(transcript.Elapsed.TotalSeconds, 1),
            engine = S.Describe(choice)?.Label ?? choice.Key,
            savedTo = autoSave,
        };
    }

    private void OnMeetingTick(object? sender, EventArgs e) =>
        Push("meeting.tick", new { seconds = Math.Floor(Meeting.Elapsed.TotalSeconds), level = Math.Round(Meeting.Level, 3) });

    /// <summary>ملخص + نقاط + قرارات + مهام، ويُحفظ بجانب النص.</summary>
    private async Task<object> SummarizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("لا يوجد نص للتلخيص.");
        }

        var rewriter = S.CreateRewriter() ?? throw new InvalidOperationException("التلخيص يحتاج مفتاح Gemini (مجاني). أضفه من صفحة النماذج.");
        _fileCts?.Cancel();
        _fileCts = new CancellationTokenSource();
        var summary = await rewriter.SummarizeAsync(text, _fileCts.Token);
        var source = _lastTranscript?.FilePath;
        var name = (source is null ? "تفريغ" : Path.GetFileNameWithoutExtension(source)) + " - ملخص.txt";
        var saved = Path.Combine(AppPaths.Ensure(AppPaths.Transcripts), name);
        TranscriptExporter.SaveText(saved, summary);
        return new { summary, savedTo = saved };
    }

    private object? ExportTranscript(string format, string text)
    {
        var source = _lastTranscript?.FilePath;
        var baseName = source is null ? "تفريغ" : Path.GetFileNameWithoutExtension(source);
        var (filter, extension) = format switch
        {
            "docx" => ("مستند Word (*.docx)|*.docx", ".docx"),
            "srt" => ("ترجمة SRT (*.srt)|*.srt", ".srt"),
            _ => ("ملف نصي (*.txt)|*.txt", ".txt"),
        };

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
                TranscriptExporter.SaveSrt(dialog.FileName, _lastTranscript?.Segments
                    ?? throw new InvalidOperationException("لا توجد توقيتات. فرّغ الملف أولًا."));
                break;
            default:
                TranscriptExporter.SaveText(dialog.FileName, text);
                break;
        }

        return new { path = dialog.FileName };
    }

    // ===== أدوات =====
    private static string? Str(JsonObject p, string name) =>
        p[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Required(JsonObject p, string name) =>
        Str(p, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing '{name}'.");
}
