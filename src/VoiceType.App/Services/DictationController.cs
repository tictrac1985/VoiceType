using System.IO;
using System.Text;
using System.Windows.Threading;
using NAudio.Wave;
using VoiceType.Core.Speech;
using VoiceType.Providers.Live;
using VoiceType.Providers.Rewrite;
using VoiceType.Settings;
using VoiceType.Providers.Resilience;
using VoiceType.Text;

namespace VoiceType.App.Services;

public enum DictationState
{
    Idle,
    Recording,
    Processing,
}

public enum DictationTarget
{
    /// <summary>يكتب النص في البرنامج الذي عليه المؤشر.</summary>
    ActiveApp,

    /// <summary>يعيد النص فقط (مثل خانة التجربة داخل VoiceType).</summary>
    ReturnOnly,
}

/// <summary>جملة في السجل. Note توضح إن استُخدم مفتاح آخر أو النموذج البديل ولماذا.</summary>
public sealed record HistoryItem(int Id, DateTime Time, string Text, string Engine, bool Inserted, double Seconds, string? Note = null);

/// <summary>
/// الإملاء: تسجيل ← تفريغ (الأساسي ثم البديل) ← تنسيق ← كتابة في البرنامج الحالي ← السجل.
/// كل الدوال تُستدعى من خيط الواجهة.
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly AppServices _services;
    private readonly AudioRecorder _recorder = new();
    private readonly List<HistoryItem> _history = [];
    private readonly TextProcessor _processor = new();
    private DictationTarget _target;
    private CancellationTokenSource? _cts;
    private int _nextId = 1;

    // ===== الكتابة المباشرة أثناء الكلام =====
    private const string LiveEngineLabel = "Google Gemini · Live";
    private readonly SynchronizationContext? _ui;
    private readonly LiveSegmenter _segmenter = new();
    private readonly StringBuilder _liveRaw = new();
    private LiveTranscriber? _live;
    private Task _liveStart = Task.CompletedTask;
    private Task _liveInserts = Task.CompletedTask;
    private DispatcherTimer? _liveTimer;
    private string _liveText = string.Empty;
    private bool _liveInsertFailed;
    private readonly List<string> _liveChunks = [];
    private int _liveCommands;

    // ===== حسب البرنامج الحالي =====
    private int _dictationId;
    private TextMode _textMode = TextMode.General;
    private LineBreakMode _lineBreaks = LineBreakMode.Sentences;
    private WritingMode _writing = WritingMode.Normal;
    private string? _profileName;
    private readonly List<string> _recentApps = [];

    /// <summary>آخر البرامج التي كُتب فيها (لصفحة الإعدادات).</summary>
    public IReadOnlyList<string> RecentApps => _recentApps;

    public DictationController(AppServices services)
    {
        _services = services;
        _ui = SynchronizationContext.Current;
        _recorder.Pcm += (buffer, count) => _live?.SendAudio(buffer, count);
        _recorder.LevelChanged += level => LevelChanged?.Invoke(level);
        _recorder.Failed += failure =>
        {
            if (State != DictationState.Recording)
            {
                return;
            }

            SetState(DictationState.Idle);
            AbortLive();
            Sounds.Error();
            const string message = "انقطع الميكروفون أثناء التسجيل. تأكد أنه متصل وحاول مرة أخرى.";
            Notice?.Invoke(message, true);
            if (_target == DictationTarget.ReturnOnly)
            {
                TestResult?.Invoke(null, message);
            }
        };
    }

    public event Action<DictationState>? StateChanged;

    public event Action<double>? LevelChanged;

    /// <summary>رسالة قصيرة للمستخدم (النص، هل هي خطأ).</summary>
    public event Action<string, bool>? Notice;

    public event Action<HistoryItem>? HistoryAdded;

    /// <summary>نتيجة التسجيل في وضع ReturnOnly.</summary>
    public event Action<string?, string?>? TestResult;

    /// <summary>أثناء الكلام: النص الذي كُتب حتى الآن، والكلام الحالي قبل اكتماله.</summary>
    public event Action<string, string>? LiveText;

    public DictationState State { get; private set; }

    public DictationTarget Target => _target;

    public IReadOnlyList<HistoryItem> History => _history;

    public void RemoveHistory(int id) => _history.RemoveAll(h => h.Id == id);

    public void ClearHistory() => _history.Clear();

    public async Task ToggleAsync(DictationTarget target = DictationTarget.ActiveApp)
    {
        switch (State)
        {
            case DictationState.Idle:
                Start(target);
                break;
            case DictationState.Recording:
                await StopAsync();
                break;
        }
    }

    public void Start(DictationTarget target)
    {
        if (State != DictationState.Idle)
        {
            return;
        }

        try
        {
            _target = target;
            PrepareTarget(target);
            StartLive(); // قبل الميكروفون حتى يصل كل الصوت من أول لحظة
            try
            {
                _recorder.Start(_services.Settings.MicrophoneDevice);
            }
            catch (InvalidOperationException)
            {
                AbortLive();
                throw;
            }

            Sounds.Start();
            SetState(DictationState.Recording);

            // تحميل النموذج المحلي أثناء الكلام، حتى يكون جاهزًا لو احتجناه
            try
            {
                _services.WarmUpLocal();
            }
            catch (Exception ex) when (ex is SpeechException or IOException or InvalidOperationException)
            {
            }
        }
        catch (InvalidOperationException ex)
        {
            Sounds.Error();
            Notice?.Invoke(ex.Message, true);
            if (target == DictationTarget.ReturnOnly)
            {
                TestResult?.Invoke(null, ex.Message);
            }
        }
    }

    public void Cancel()
    {
        if (State == DictationState.Recording)
        {
            _recorder.Cancel();
            AbortLive();
            SetState(DictationState.Idle);
            Notice?.Invoke("أُلغي التسجيل.", false);
            if (_target == DictationTarget.ReturnOnly)
            {
                TestResult?.Invoke(null, "أُلغي التسجيل.");
            }
        }
        else if (State == DictationState.Processing)
        {
            _cts?.Cancel();
        }
    }

    public async Task StopAsync()
    {
        if (State != DictationState.Recording)
        {
            return;
        }

        var duration = DateTime.Now - _recorder.StartedAt;
        var averageLevel = _recorder.AverageLevel;
        SetState(DictationState.Processing);
        Sounds.Stop();

        string? text = null;
        string? error = null;
        var remainderFailed = false;
        _cts = new CancellationTokenSource(); // للإلغاء من المستخدم فقط؛ كل خدمة لها مهلتها، فيعمل البديل دائمًا
        try
        {
            var wav = await _recorder.StopAsync();

            // ١) الكتابة المباشرة: ما قيل كُتب بالفعل أثناء الكلام، ننتظر فقط الجملة الأخيرة
            var live = await FinishLiveAsync(_cts.Token);
            var liveText = live.Text;
            var liveHandled = liveText.Length > 0 || _liveCommands > 0;
            byte[]? batchWav = wav;
            if (liveHandled)
            {
                // لم يصل نص آخر جزء (انقطاع، أو كلام بعد آخر جملة): نفرّغه بالطريقة العادية
                var incomplete = !live.Complete || TailHasSpeech(wav, live.BytesAtLastFinal);
                batchWav = incomplete ? RemainderWav(wav, live.BytesAtLastFinal) : null;
                if (incomplete && batchWav is null && live.PendingInterim.Length > 0)
                {
                    Commit(live.PendingInterim);
                    await _liveInserts;
                    liveText = _liveText;
                }
            }
            else if (duration < TimeSpan.FromSeconds(0.6) || wav.Length < 16000)
            {
                error = "التسجيل قصير جدًا. انقر وتكلم، ثم انقر مرة أخرى عند الانتهاء.";
                return;
            }
            else if (averageLevel < 0.008)
            {
                error = "لم أسمع كلامًا. تأكد أن الميكروفون يعمل وقريب منك.";
                return;
            }

            // ٢) الطريقة العادية: لكل التسجيل إن لم تعمل الكتابة المباشرة، أو للجزء الأخير إن انقطعت
            TranscriptionResult? result = null;
            var batchText = string.Empty;
            var started = System.Diagnostics.Stopwatch.StartNew();
            if (batchWav is not null)
            {
                try
                {
                    // الجزء الأخير قصير، لكن نعطيه مهلة كاملة حتى لا تُحسب الخدمة «بطيئة» وتُتخطى في الإملاء التالي
                    var batchDuration = duration;
                    var engine = _services.CreateDictationEngine(batchDuration);
                    result = await engine.TranscribeAsync(new AudioPayload(batchWav, "wav", "dictation.wav"), _services.TranscriptionOptions, _cts.Token);
                    var usedLocalBatch = AppServices.IsLocal(ProviderOf(result.EngineId));
                    var rawBatch = usedLocalBatch ? _services.Learned.Apply(result.Text) : result.Text;
                    batchText = Hallucinations.Clean(_processor.Process(Hallucinations.Clean(rawBatch), _textMode, _lineBreaks));
                    if (IsHallucination(batchText.Trim()))
                    {
                        batchText = string.Empty;
                    }

                    if (liveText.Length > 0)
                    {
                        batchText = RemoveOverlap(liveText, batchText); // الجزء الأخير يبدأ قبل آخر جملة بقليل
                    }
                }
                catch (Exception ex) when (liveText.Length > 0 && ex is not OutOfMemoryException)
                {
                    // ما كُتب مباشرة محفوظ؛ فقط الجزء الأخير لم يُفرّغ
                    result = null;
                    batchText = string.Empty;
                    remainderFailed = ex is not OperationCanceledException;
                    DictationLog.Write(duration, TimeSpan.Zero, null, [], "remainder failed: " + ex.Message);
                }
            }

            started.Stop();
            DictationLog.Write(duration, started.Elapsed, liveText.Length > 0 ? "gemini:" + LiveOptions.DefaultModel : result?.EngineId,
                result?.PreviousFailures ?? [], liveText.Length > 0 && !live.Complete ? "live incomplete: " + live.Error : null);

            // أمر صوتي قيل وحده («امسح آخر جملة»…)
            if (!liveHandled && VoiceCommands.Parse(batchText) is var command && command != VoiceCommand.None)
            {
                await ExecuteCommandAsync(command);
                return;
            }

            var separator = LiveSegmenter.Separator(_lineBreaks == LineBreakMode.Sentences, liveText, batchText);
            text = liveText.Length == 0 ? batchText : batchText.Length == 0 ? liveText : liveText + separator + batchText;
            if (string.IsNullOrWhiteSpace(text) || IsHallucination(text.Trim()))
            {
                text = null;
                if (!liveHandled)
                {
                    error = "لم أسمع كلامًا واضحًا.";
                }

                return;
            }

            // «صيغها لي»: رسمي، إيميل، أو ترجمة — للنص كله بعد انتهاء الكلام
            string? rewriteNote = null;
            if (_writing != WritingMode.Normal)
            {
                Notice?.Invoke(_writing == WritingMode.English ? "جارٍ الترجمة…" : "جارٍ الصياغة…", false);
                try
                {
                    var rewriter = _services.CreateRewriter(TimeSpan.FromSeconds(40)) ?? throw new InvalidOperationException("أضف مفتاح Gemini للصياغة.");
                    text = await rewriter.RewriteAsync(text, _writing, _cts.Token);
                    rewriteNote = _writing switch
                    {
                        WritingMode.Formal => "صيغ بالفصحى الرسمية.",
                        WritingMode.Email => "صيغ كرسالة بريد.",
                        _ => "تُرجم إلى الإنجليزية.",
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
                {
                    rewriteNote = "تعذّرت الصياغة، فكُتب الكلام كما قيل.";
                    DictationLog.Write(duration, TimeSpan.Zero, null, [], "rewrite failed: " + ex.Message);
                }
            }

            var usedLocal = result is not null && liveText.Length == 0 && AppServices.IsLocal(ProviderOf(result.EngineId));
            if (!usedLocal)
            {
                LearnInBackground(wav, (live.Raw + " " + (result?.Text ?? string.Empty)).Trim(), duration);
            }

            var engineName = liveText.Length > 0 ? LiveEngineLabel : EngineName(result!.EngineId);
            var note = string.Join(" ", new[] { _profileName is null ? null : "إعداد: " + _profileName + ".", rewriteNote,
                result is null ? null : DescribeFailures(result.PreviousFailures, result.EngineId) }.Where(n => !string.IsNullOrEmpty(n)));
            if (note.Length == 0)
            {
                note = null;
            }
            var usedFallback = result is not null && result.PreviousFailures.Any(f => !SameProvider(f.EngineId, result.EngineId));
            // مع الصياغة لا يُكتب شيء أثناء الكلام: النص كله يُكتب الآن
            var writeAll = _writing != WritingMode.Normal;
            var inserted = writeAll || liveText.Length == 0 || !_liveInsertFailed;
            if (_target == DictationTarget.ActiveApp && (writeAll || batchText.Length > 0))
            {
                try
                {
                    var chunk = writeAll ? text : liveText.Length > 0 ? separator + batchText : batchText;
                    inserted = await TextInjector.InsertAsync(chunk, _dictationId) && inserted;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // لا نفقد النص أبدًا: يبقى في السجل للنسخ
                    inserted = false;
                    DictationLog.Write(duration, TimeSpan.Zero, result?.EngineId, [], "insert failed: " + ex.Message);
                }
            }

            if (_target != DictationTarget.ActiveApp)
            {
                inserted = false;
            }
            else if (!inserted)
            {
                DictationLog.Write(duration, TimeSpan.Zero, result?.EngineId, [],
                    $"insert failed: {TextInjector.LastFailure} app={TextInjector.LastFailureApp ?? "-"}");
            }

            var item = new HistoryItem(_nextId++, DateTime.Now, text, engineName, inserted, duration.TotalSeconds, note);
            _history.Insert(0, item);
            if (_history.Count > 50)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            HistoryAdded?.Invoke(item);
            if (_target == DictationTarget.ActiveApp)
            {
                var failedName = result is { PreviousFailures.Count: > 0 } ? ProviderName(result.PreviousFailures[0].EngineId) : null;
                Notice?.Invoke(
                    !inserted ? TextInjector.FailureMessage
                    : remainderFailed ? "انقطع الاتصال، فلم يُكتب آخر جزء من الكلام."
                    : usedFallback ? $"✓ كُتب بـ{ShortEngine(engineName)} — {failedName} لم يعمل" : "✓ تمت الكتابة",
                    !inserted || remainderFailed);
            }
        }
        catch (AllEnginesFailedException ex)
        {
            error = ex.Failures.Count > 0 ? ex.Failures[^1].Message : ex.UserMessage;
            DictationLog.Write(duration, TimeSpan.Zero, null, ex.Failures, error);
        }
        catch (SpeechException ex)
        {
            error = ex.UserMessage;
        }
        catch (OperationCanceledException)
        {
            error = "أُلغي التفريغ.";
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = "حدث خطأ غير متوقع: " + ex.Message;
            DictationLog.Write(duration, TimeSpan.Zero, null, [], ex.ToString());
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await StopLiveAsync();
            TextInjector.EndBatch();
            if (error is not null)
            {
                Sounds.Error();
                Notice?.Invoke(error, true);
            }

            if (_target == DictationTarget.ReturnOnly)
            {
                TestResult?.Invoke(text, error);
            }

            SetState(DictationState.Idle);
        }
    }

    // =====================================================================
    //  الكتابة المباشرة أثناء الكلام (Gemini Live)
    // =====================================================================

    private void StartLive()
    {
        AbortLive();
        _liveText = string.Empty;
        _liveChunks.Clear();
        _liveCommands = 0;
        _liveRaw.Clear();
        _segmenter.Flush();
        _liveInsertFailed = false;
        _liveInserts = Task.CompletedTask;
        _liveStart = Task.CompletedTask;

        LiveTranscriber? live;
        try
        {
            live = _services.CreateLiveTranscriber();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            live = null;
        }

        if (live is null)
        {
            return;
        }

        _live = live;
        live.Interim += interim => Post(() => OnInterim(live, interim));
        live.Final += final => Post(() => OnFinal(live, final));
        live.Failed += reason => Post(() => DictationLog.Write(TimeSpan.Zero, TimeSpan.Zero, "gemini:" + LiveOptions.DefaultModel, [], "live failed: " + reason));
        if (_target == DictationTarget.ActiveApp)
        {
            TextInjector.BeginBatch();
        }

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _liveTimer.Tick += (_, _) =>
        {
            if (ReferenceEquals(_live, live) && _segmenter.Tick(DateTime.UtcNow) is { } sentence)
            {
                Commit(sentence);
            }
        };
        _liveTimer.Start();
        _liveStart = StartLiveAsync(live);
    }

    private async Task StartLiveAsync(LiveTranscriber live)
    {
        try
        {
            await live.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // أُلغي التسجيل أثناء الاتصال: ليس عطلًا
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // لا تعمل الآن (مفتاح، شبكة، أو الخدمة): نتخطاها لدقائق ونكمل بالطريقة العادية
            EngineHealth.Shared.Failure(AppServices.LiveEngineId, SpeechErrorKind.Timeout);
            DictationLog.Write(TimeSpan.Zero, TimeSpan.Zero, "gemini:" + LiveOptions.DefaultModel, [], "live unavailable: " + (live.Error ?? ex.Message));
        }
    }

    private void OnInterim(LiveTranscriber live, string interim)
    {
        if (ReferenceEquals(_live, live))
        {
            LiveText?.Invoke(_liveText, interim);
        }
    }

    private void OnFinal(LiveTranscriber live, string final)
    {
        if (!ReferenceEquals(_live, live))
        {
            return;
        }

        _liveRaw.Append(final).Append(' ');
        foreach (var sentence in _segmenter.Add(final, DateTime.UtcNow))
        {
            Commit(sentence);
        }

        LiveText?.Invoke(_liveText, string.Empty);
    }

    /// <summary>جملة اكتملت: تُنسّق وتُكتب فورًا في البرنامج (بالترتيب، واحدة بعد الأخرى).</summary>
    private void Commit(string raw)
    {
        // أمر صوتي قيل وحده: يُنفّذ بدل أن يُكتب
        var command = VoiceCommands.Parse(raw);
        if (command != VoiceCommand.None)
        {
            _liveCommands++;
            _liveInserts = CommandAfterAsync(_liveInserts, command);
            return;
        }

        var processed = Hallucinations.Clean(_processor.Process(Hallucinations.Clean(raw), _textMode, _lineBreaks));
        if (string.IsNullOrWhiteSpace(processed) || IsHallucination(processed.Trim()))
        {
            return;
        }

        var chunk = LiveSegmenter.Separator(_lineBreaks == LineBreakMode.Sentences, _liveText, processed) + processed;
        _liveText += chunk;
        _liveChunks.Add(chunk);
        if (_target == DictationTarget.ActiveApp && _writing == WritingMode.Normal)
        {
            _liveInserts = InsertAfterAsync(_liveInserts, chunk);
        }

        LiveText?.Invoke(_liveText, string.Empty);
    }

    private async Task CommandAfterAsync(Task previous, VoiceCommand command)
    {
        await previous;
        try
        {
            await ExecuteCommandAsync(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DictationLog.Write(TimeSpan.Zero, TimeSpan.Zero, null, [], "command failed: " + ex.Message);
        }
    }

    /// <summary>
    /// ينفّذ أوامر التعديل: في البرنامج نفسه بالتراجع (Ctrl+Z يلغي لصقة كاملة)، أو على النص المجمّع إذا لم يُكتب بعد (وضع الصياغة).
    /// </summary>
    private async Task ExecuteCommandAsync(VoiceCommand command)
    {
        // النص لم يُكتب في البرنامج بعد (صياغة أو خانة التجربة): نعدّل النص المجمّع فقط
        if (_target != DictationTarget.ActiveApp || (_writing != WritingMode.Normal && _liveChunks.Count > 0))
        {
            EditLiveText(command);
            Notice?.Invoke(CommandDone(command), false);
            return;
        }

        var window = TextInjector.ForegroundWindow();
        var recent = TextInjector.Insertions
            .Where(i => i.Window == window && DateTime.UtcNow - i.Time < TimeSpan.FromMinutes(15))
            .ToList();
        if (recent.Count == 0)
        {
            Notice?.Invoke("لا يوجد ما يُمسح هنا.", false);
            return;
        }

        switch (command)
        {
            case VoiceCommand.DeleteLastSentence:
                await TextInjector.UndoAsync(1);
                TextInjector.ForgetInsertion(recent[^1]);
                if (recent[^1].Dictation == _dictationId)
                {
                    EditLiveText(command);
                }

                break;
            case VoiceCommand.DeleteLastWord:
                await TextInjector.DeleteWordAsync();
                if (recent[^1].Dictation == _dictationId)
                {
                    EditLiveText(command);
                }

                break;
            case VoiceCommand.DeleteAll:
                var dictation = recent[^1].Dictation;
                var items = recent.Where(i => i.Dictation == dictation).ToList();
                await TextInjector.UndoAsync(items.Count);
                items.ForEach(TextInjector.ForgetInsertion);
                if (dictation == _dictationId)
                {
                    EditLiveText(command);
                }

                break;
        }

        Notice?.Invoke(CommandDone(command), false);
    }

    private static string CommandDone(VoiceCommand command) => command switch
    {
        VoiceCommand.DeleteLastWord => "✓ مُسحت آخر كلمة",
        VoiceCommand.DeleteAll => "✓ مُسح كل ما كُتب",
        _ => "✓ مُسحت آخر جملة",
    };

    /// <summary>يعدّل النص المجمّع للإملاء الحالي ليطابق ما مُسح.</summary>
    private void EditLiveText(VoiceCommand command)
    {
        switch (command)
        {
            case VoiceCommand.DeleteAll:
                _liveChunks.Clear();
                break;
            case VoiceCommand.DeleteLastSentence when _liveChunks.Count > 0:
                _liveChunks.RemoveAt(_liveChunks.Count - 1);
                break;
            case VoiceCommand.DeleteLastWord when _liveChunks.Count > 0:
                var last = _liveChunks[^1].TrimEnd();
                var cut = last.LastIndexOfAny([' ', '\n']);
                if (cut <= 0 || last[..cut].Trim().Length == 0)
                {
                    _liveChunks.RemoveAt(_liveChunks.Count - 1);
                }
                else
                {
                    _liveChunks[^1] = last[..cut];
                }

                break;
        }

        _liveText = string.Concat(_liveChunks);
        LiveText?.Invoke(_liveText, string.Empty);
    }

    /// <summary>البرنامج الذي سيُكتب فيه، والإعداد الخاص به (محادثات، مستندات، أكواد).</summary>
    private void PrepareTarget(DictationTarget target)
    {
        _dictationId++;
        var settings = _services.Settings;
        string? process = null;
        string? title = null;
        if (target == DictationTarget.ActiveApp)
        {
            TextInjector.CaptureTarget();
            process = TextInjector.ForegroundProcessName();
            title = TextInjector.ForegroundTitle();
            if (process is not null && !string.Equals(process, "VoiceType", StringComparison.OrdinalIgnoreCase))
            {
                _recentApps.Remove(process);
                _recentApps.Insert(0, process);
                if (_recentApps.Count > 10)
                {
                    _recentApps.RemoveAt(_recentApps.Count - 1);
                }
            }
        }

        var profile = target == DictationTarget.ActiveApp && settings.UseAppProfiles ? AppProfile.Find(settings.Profiles, process, title) : null;
        _profileName = profile?.Name;
        var raw = _services.TextMode == TextMode.Raw;
        (_textMode, _lineBreaks) = profile?.Style switch
        {
            "chat" => (raw ? TextMode.Raw : TextMode.Technical, LineBreakMode.None),      // بدون نقطة في آخر الرسالة
            "document" => (raw ? TextMode.Raw : TextMode.General, LineBreakMode.Sentences),
            "code" => (TextMode.Technical, LineBreakMode.None),
            _ => (_services.TextMode, _services.LineBreaks),
        };
        _writing = TextRewriter.ParseMode(profile?.Writing ?? settings.WritingMode);
    }

    private async Task InsertAfterAsync(Task previous, string chunk)
    {
        await previous;
        try
        {
            if (!await TextInjector.InsertAsync(chunk, _dictationId))
            {
                _liveInsertFailed = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _liveInsertFailed = true;
        }
    }

    private sealed record LiveOutcome(string Text, string Raw, bool Complete, long BytesAtLastFinal, string PendingInterim, string? Error);

    /// <summary>ينهي الجلسة المباشرة ويكتب آخر جملة. Complete = كل ما وصل للخدمة فُرّغ بدون انقطاع.</summary>
    private async Task<LiveOutcome> FinishLiveAsync(CancellationToken cancellationToken)
    {
        var live = _live;
        if (live is null)
        {
            return new LiveOutcome(string.Empty, string.Empty, false, 0, string.Empty, null);
        }

        var ok = false;
        try
        {
            // لا ننتظر اتصالًا بطيئًا طويلًا: الطريقة العادية تكمل
            await Task.WhenAny(_liveStart, Task.Delay(2500, cancellationToken));
            if (live.IsReady)
            {
                var finish = live.FinishAsync(TimeSpan.FromSeconds(4));
                await Task.WhenAny(finish, Task.Delay(Timeout.Infinite, cancellationToken));
                ok = finish.IsCompletedSuccessfully && finish.Result;
            }

            await Task.Delay(40, CancellationToken.None); // نترك آخر نص وصل يُعالج قبل الفصل
        }
        catch (OperationCanceledException)
        {
            ok = false;
        }

        // نفصل الجلسة، ثم نكتب ما تبقى بالترتيب
        _live = null;
        _liveTimer?.Stop();
        if (_segmenter.Flush() is { } rest)
        {
            Commit(rest);
        }

        await _liveInserts;
        var pending = live.PendingInterim;
        var outcome = new LiveOutcome(_liveText, _liveRaw.ToString().Trim(), ok && pending.Length == 0, live.BytesAtLastFinal, pending, live.Error);
        _ = live.DisposeAsync().AsTask();
        return outcome;
    }

    /// <summary>إيقاف فوري (إلغاء، أو انقطاع الميكروفون). ما كُتب يبقى في السجل.</summary>
    private void AbortLive()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        var live = _live;
        _live = null;
        if (live is not null)
        {
            _ = live.DisposeAsync().AsTask();
            if (_liveText.Length > 0)
            {
                AddHistory(_liveText, LiveEngineLabel, !_liveInsertFailed && _target == DictationTarget.ActiveApp, DateTime.Now - _recorder.StartedAt, "أُلغي قبل النهاية.");
            }
        }

        _segmenter.Flush();
        TextInjector.EndBatch();
    }

    private async Task StopLiveAsync()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        var live = _live;
        _live = null;
        if (live is not null)
        {
            try
            {
                await live.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
    }

    private void AddHistory(string text, string engine, bool inserted, TimeSpan duration, string? note)
    {
        var item = new HistoryItem(_nextId++, DateTime.Now, text, engine, inserted, duration.TotalSeconds, note);
        _history.Insert(0, item);
        if (_history.Count > 50)
        {
            _history.RemoveAt(_history.Count - 1);
        }

        HistoryAdded?.Invoke(item);
    }

    /// <summary>هل في هذا الجزء من التسجيل كلام (وليس صمتًا)؟</summary>
    private static bool TailHasSpeech(byte[] wav, long fromBytes)
    {
        var dataStart = FindDataChunk(wav);
        if (dataStart < 0)
        {
            return false;
        }

        var loud = 0;
        const int frame = 640; // 20 ملّي ثانية
        for (var i = dataStart + (int)Math.Max(0, fromBytes - (fromBytes % 2)); i + frame <= wav.Length; i += frame)
        {
            var peak = 0;
            for (var j = i; j + 1 < i + frame; j += 2)
            {
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, j)));
            }

            if (peak > 2600 && ++loud >= 10) // ~ ٢٠٠ ملّي ثانية من الصوت الواضح
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>يحذف من بداية النص الأخير ما تكرر من نهاية النص المكتوب (بسبب التداخل الصغير في الصوت).</summary>
    internal static string RemoveOverlap(string written, string next)
    {
        static string Norm(string w) => new(w.Where(char.IsLetterOrDigit).ToArray());
        var a = written.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(w => w.Length > 0).ToArray();
        var bWords = next.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var b = bWords.Select(Norm).ToArray();
        for (var n = Math.Min(12, Math.Min(a.Length, b.Length)); n >= 2; n--)
        {
            if (a[^n..].SequenceEqual(b[..n], StringComparer.OrdinalIgnoreCase))
            {
                return string.Join(' ', bWords[n..]);
            }
        }

        return next;
    }

    /// <summary>
    /// الجزء من التسجيل الذي لم يصل نصه (بعد آخر جملة كُتبت)، مع نصف ثانية قبله حتى لا تضيع كلمة.
    /// null إذا كان قصيرًا جدًا.
    /// </summary>
    private static byte[]? RemainderWav(byte[] wav, long bytesAtLastFinal)
    {
        var dataStart = FindDataChunk(wav);
        if (dataStart < 0)
        {
            return null;
        }

        var start = Math.Max(0, bytesAtLastFinal - 48000); // ١٫٥ ثانية قبلها حتى لا تضيع كلمة
        start -= start % 2;
        var available = wav.Length - dataStart - start;
        if (available < 16000 * 2) // أقل من ثانية: غالبًا صمت
        {
            return null;
        }

        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(output), new WaveFormat(16000, 16, 1)))
        {
            writer.Write(wav, (int)(dataStart + start), (int)available);
        }

        return output.ToArray();
    }

    private static int FindDataChunk(byte[] wav)
    {
        for (var i = 12; i + 8 <= wav.Length; i++)
        {
            if (wav[i] == (byte)'d' && wav[i + 1] == (byte)'a' && wav[i + 2] == (byte)'t' && wav[i + 3] == (byte)'a')
            {
                return i + 8;
            }
        }

        return -1;
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private readonly SemaphoreSlim _learning = new(1, 1);

    /// <summary>
    /// التعلّم من Gemini: يشغّل النموذج المحلي على نفس التسجيل في الخلفية ويقارن النصين.
    /// لا يؤخر الإملاء، ويتخطى إن كان هناك تعلّم آخر جارٍ أو التسجيل طويل.
    /// </summary>
    private void LearnInBackground(byte[] wav, string referenceText, TimeSpan duration)
    {
        if (!_services.Settings.LearnFromOnline || duration > TimeSpan.FromSeconds(90) || string.IsNullOrWhiteSpace(referenceText) ||
            _services.LearningLocalModel() is not { } localChoice || !_learning.Wait(0))
        {
            return;
        }

        ISpeechEngine engine;
        TranscriptionOptions options;
        try
        {
            engine = _services.CreateEngine(localChoice);
            options = _services.TranscriptionOptions;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _learning.Release();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var local = await engine.TranscribeAsync(new AudioPayload(wav, "wav", "learning.wav"), options).ConfigureAwait(false);
                var learned = _services.Learned.Learn(local.Text, referenceText);
                if (learned > 0)
                {
                    LearningUpdated?.Invoke();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DictationLog.Write(duration, TimeSpan.Zero, null, [], "learning skipped: " + ex.Message);
            }
            finally
            {
                _learning.Release();
            }
        });
    }

    /// <summary>تعلّم البرنامج شيئًا جديدًا (لتحديث صفحة الإعدادات).</summary>
    public event Action? LearningUpdated;

    private string EngineName(string engineId) =>
        _services.Describe(VoiceType.Settings.EngineChoice.Parse(engineId.Replace(':', '|')))?.Label ?? engineId;

    private static string ProviderOf(string engineId)
    {
        var colon = engineId.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? engineId : engineId[..colon];
    }

    private static bool SameProvider(string failedId, string usedId) =>
        string.Equals(ProviderOf(failedId), ProviderOf(usedId), StringComparison.OrdinalIgnoreCase);

    /// <summary>سطر يوضح للمستخدم ما حدث قبل النجاح (مفتاح تجاوز حده، أو انتقال للنموذج المحلي).</summary>
    private string? DescribeFailures(IReadOnlyList<EngineFailure> failures, string usedId)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var parts = failures.Select(f =>
        {
            var name = f.EngineId.Contains('(') ? ProviderName(f.EngineId) + " " + f.EngineId[f.EngineId.IndexOf('(')..] : ProviderName(f.EngineId);
            var detail = f.Kind is SpeechErrorKind.BadRequest or SpeechErrorKind.Unknown or SpeechErrorKind.InvalidResponse or SpeechErrorKind.NotFound
                         && !string.IsNullOrWhiteSpace(f.Details)
                ? " (" + ShortDetails(f.Details!) + ")"
                : string.Empty;
            return $"{name}: {f.Message}{detail}";
        });
        var tail = failures.All(f => SameProvider(f.EngineId, usedId)) ? "فاستُخدم مفتاح آخر." : "فاستُخدم البديل.";
        return string.Join(" — ", parts.Distinct()) + " " + tail;
    }

    private static string ShortEngine(string label) =>
        label.StartsWith("على جهازك", StringComparison.Ordinal) ? "النموذج المحلي" : label.Split(" · ")[0];

    private static string ShortDetails(string details)
    {
        var text = details.StartsWith("HTTP ", StringComparison.Ordinal) && details.IndexOf(':') is > 0 and var colon ? details[(colon + 1)..].Trim() : details;
        return text.Length <= 90 ? text : text[..90] + "…";
    }

    private string ProviderName(string engineId)
    {
        var provider = ProviderOf(engineId);
        if (AppServices.IsLocal(provider))
        {
            return "النموذج المحلي";
        }

        return _services.Catalog.All.FirstOrDefault(d => string.Equals(d.Id, provider, StringComparison.OrdinalIgnoreCase))?.Name ?? provider;
    }

    private static bool IsHallucination(string text) => Hallucinations.IsOnlyHallucination(text);

    private void SetState(DictationState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        AbortLive();
        _recorder.Dispose();
    }
}
