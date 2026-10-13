using System.Collections.Concurrent;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Resilience;
using VoiceType.Text;

namespace VoiceType.Evaluation.Comparison;

public sealed record SampleFile(string Path, string Name, string? Reference);

/// <summary>نموذج مطلوب مقارنته: مزود + نموذج + مفتاح.</summary>
public sealed record CompareTarget(
    string DefinitionId,
    string? Model,
    string? ApiKey,
    string? Label = null,
    IReadOnlyDictionary<string, string>? Variables = null,
    int? TimeoutSeconds = null);

public sealed record ProviderRun(string Label, string DefinitionId, string? Model, string? SkipReason);

public sealed record FileResult(
    ProviderRun Provider,
    SampleFile Sample,
    string? Text,
    string? TechnicalText,
    TimeSpan Elapsed,
    double? Cost,
    SpeechErrorKind? ErrorKind,
    string? ErrorDetails,
    TranscriptScore? Score,
    TranscriptScore? TechnicalScore);

public sealed record CompareProgress(int Completed, int Total, string Label, string FileName, bool Success, string Message);

/// <summary>
/// يرسل كل مقطع إلى كل نموذج ويحسب الدقة. يُستخدم من أداة سطر الأوامر ومن برنامج المختبر.
/// </summary>
public sealed class ComparisonRunner(
    ProviderCatalog catalog,
    HttpClient http,
    Func<CompareTarget, ISpeechEngine?>? localEngineFactory = null)
{
    /// <summary>معرّف "المزود" للنماذج المحلية على جهاز المستخدم.</summary>
    public const string LocalProviderId = "local";

    public ProviderCatalog Catalog { get; } = catalog;

    public static IReadOnlyList<SampleFile> LoadSamples(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(dataDirectory)
            .Where(AudioFormats.IsAudioFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var referencePath = System.IO.Path.ChangeExtension(path, ".txt");
                var reference = File.Exists(referencePath) ? File.ReadAllText(referencePath).Trim() : null;
                return new SampleFile(path, System.IO.Path.GetFileName(path), string.IsNullOrWhiteSpace(reference) ? null : reference);
            })
            .ToList();
    }

    public static string LabelOf(CompareTarget target) =>
        target.Label ?? (target.Model is null ? target.DefinitionId : $"{target.DefinitionId}:{target.Model}");

    /// <summary>يجهز المحرك، أو يعيد سبب التخطي (مزود غير معروف، مفتاح ناقص).</summary>
    public (ProviderRun Run, ISpeechEngine? Engine) Prepare(CompareTarget target)
    {
        var label = LabelOf(target);
        if (string.Equals(target.DefinitionId, LocalProviderId, StringComparison.OrdinalIgnoreCase))
        {
            var local = localEngineFactory?.Invoke(target);
            return local is null
                ? (new ProviderRun(label, target.DefinitionId, target.Model, "النموذج غير منزّل على الجهاز"), null)
                : (new ProviderRun(label, target.DefinitionId, target.Model, null), local);
        }

        if (!Catalog.TryGet(target.DefinitionId, out var definition))
        {
            return (new ProviderRun(label, target.DefinitionId, target.Model, $"مزود غير معروف: {target.DefinitionId}"), null);
        }

        var auth = definition.Request.Auth;
        if (auth.Type != AuthDefinition.None && !auth.Optional && string.IsNullOrWhiteSpace(target.ApiKey))
        {
            return (new ProviderRun(label, target.DefinitionId, target.Model, "لا يوجد مفتاح لهذا المزود"), null);
        }

        var instance = new ProviderInstance
        {
            Definition = target.DefinitionId,
            Model = target.Model,
            ApiKey = target.ApiKey,
            Label = target.Label,
            TimeoutSeconds = target.TimeoutSeconds,
        };
        foreach (var (name, value) in target.Variables ?? new Dictionary<string, string>())
        {
            instance.Variables[name] = value;
        }

        var engine = new DeclarativeSpeechEngine(definition, instance, http);
        return (new ProviderRun(label, target.DefinitionId, engine.Model, null), engine);
    }

    public async Task<IReadOnlyList<FileResult>> RunAsync(
        IReadOnlyList<SampleFile> samples,
        IReadOnlyList<CompareTarget> targets,
        TranscriptionOptions options,
        int parallelism = 4,
        IProgress<CompareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var processor = new TextProcessor();
        var results = new ConcurrentBag<FileResult>();
        var prepared = targets.Select(Prepare).ToList();
        var total = prepared.Count(p => p.Engine is not null) * samples.Count;
        var completed = 0;

        foreach (var (run, _) in prepared.Where(p => p.Engine is null))
        {
            foreach (var sample in samples)
            {
                results.Add(new FileResult(run, sample, null, null, TimeSpan.Zero, null, SpeechErrorKind.Configuration, run.SkipReason, null, null));
            }
        }

        try
        {
            await RunEnginesAsync(prepared, samples, options, parallelism, processor, results, total, () => Interlocked.Increment(ref completed), progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var disposable in prepared.Select(p => p.Engine).OfType<IDisposable>())
            {
                disposable.Dispose();
            }
        }

        return results
            .OrderBy(r => r.Provider.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Sample.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task RunEnginesAsync(
        List<(ProviderRun Run, ISpeechEngine? Engine)> prepared,
        IReadOnlyList<SampleFile> samples,
        TranscriptionOptions options,
        int parallelism,
        TextProcessor processor,
        ConcurrentBag<FileResult> results,
        int total,
        Func<int> nextCompleted,
        IProgress<CompareProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            prepared.Where(p => p.Engine is not null),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                var (run, inner) = item;
                var engine = new ResilientSpeechEngine(inner!);

                foreach (var sample in samples)
                {
                    ct.ThrowIfCancellationRequested();
                    FileResult result;
                    string message;
                    try
                    {
                        var audio = AudioPayload.FromFile(sample.Path);
                        var transcription = await engine.TranscribeAsync(audio, options, ct).ConfigureAwait(false);
                        var technical = processor.Process(transcription.Text, TextMode.Technical);
                        var score = sample.Reference is null ? null : TranscriptScorer.Score(sample.Reference, transcription.Text);
                        var technicalScore = sample.Reference is null ? null : TranscriptScorer.Score(sample.Reference, technical);
                        result = new FileResult(run, sample, transcription.Text, technical, transcription.Elapsed, transcription.Cost, null, null, score, technicalScore);
                        message = $"{transcription.Elapsed.TotalSeconds:0.00}s" + (score is null ? string.Empty : $" | WER {score.Wer:P0}");
                    }
                    catch (SpeechException ex)
                    {
                        result = new FileResult(run, sample, null, null, TimeSpan.Zero, null, ex.Kind, ex.TechnicalDetails, null, null);
                        message = $"{ex.Kind}: {ex.TechnicalDetails}";
                    }
                    catch (IOException ex)
                    {
                        result = new FileResult(run, sample, null, null, TimeSpan.Zero, null, SpeechErrorKind.Unknown, ex.Message, null, null);
                        message = ex.Message;
                    }

                    results.Add(result);
                    var done = nextCompleted();
                    progress?.Report(new CompareProgress(done, total, run.Label, sample.Name, result.ErrorKind is null, message));
                }
            }).ConfigureAwait(false);
    }
}
