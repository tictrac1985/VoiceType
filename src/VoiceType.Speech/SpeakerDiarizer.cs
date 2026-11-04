using System.Text.Json.Nodes;
using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// «مين اللي بيتكلم»: بيدّي كل جملة في التفريغ رقم متكلم (المتكلم 1، المتكلم 2…).
/// بيستخدم طريقة FoxNose في CrispASR: بصمة صوت لكل جملة (WeSpeaker ResNet34)، وبعدين يجمّع الأصوات المتشابهة.
/// في تجربتنا على محادثة فيها صوتين: 17 من 19 جملة صح لما عدد المتكلمين يكون معروف.
/// نموذج البصمة (24 ميجا، رخصة CC-BY-4.0 من WeSpeaker) بينزل مرة واحدة ونتأكد من بصمته.
/// محتاج أوقات الجمل، يعني بيشتغل مع النماذج اللي على الجهاز (Cohere أو Whisper).
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    public const string EmbedderFile = "wespeaker-resnet34-lm.gguf";

    public const string EmbedderUrl = "https://huggingface.co/cstr/wespeaker-resnet34-lm-GGUF/resolve/main/" + EmbedderFile;

    public const string EmbedderSha256 = "2461a2b9a07023a1689fbdb79bd2929dd8793b8bd1ecd9f6ca3b66b4c3668d26";

    /// <summary>لما المستخدم يختار «تلقائي»: أقصى عدد نسمح بيه (الطريقة بتميل تزوّد العدد).</summary>
    public const int AutoMaxSpeakers = 4;

    private readonly CrispHostClient _host;
    private readonly string _embedderPath;

    public SpeakerDiarizer(string? embedderPath = null, string? hostPath = null, string? libraryDirectory = null)
    {
        _embedderPath = embedderPath ?? LocalModels.PathOf(EmbedderFile);
        _host = new CrispHostClient("diarize", hostPath ?? CrispRuntime.HostPath, libraryDirectory ?? CrispRuntime.Directory, modelPath: null)
        {
            LoadTimeout = TimeSpan.FromSeconds(60),
        };
    }

    public static bool IsReady => CrispRuntime.IsInstalled && LocalModels.IsInstalled(EmbedderFile);

    /// <summary>ينزّل محرك CrispASR (لو مش موجود) ونموذج بصمة الأصوات.</summary>
    public static async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!CrispRuntime.IsInstalled)
        {
            await CrispRuntime.InstallAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        if (!LocalModels.IsInstalled(EmbedderFile))
        {
            await Downloader.DownloadFileAsync(EmbedderUrl, Path.Combine(LocalModels.Folder, EmbedderFile), progress, cancellationToken,
                EmbedderSha256).ConfigureAwait(false);
        }
    }

    /// <param name="speakers">عدد المتكلمين لو معروف (2، 3…)، أو 0 = تلقائي.</param>
    public async Task<IReadOnlyList<TranscriptSegment>> LabelAsync(
        float[] samples16kMono,
        IReadOnlyList<TranscriptSegment> segments,
        int speakers = 0,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        if (!File.Exists(_embedderPath))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, "diarize", "نموذج بصمة الأصوات مش موجود. نزّله من صفحة الملفات.");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"voicetype-diarize-{Guid.NewGuid():N}.f32");
        try
        {
            var bytes = new byte[samples16kMono.Length * sizeof(float)];
            Buffer.BlockCopy(samples16kMono, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);

            await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var ranges = new JsonArray();
            foreach (var segment in segments)
            {
                ranges.Add(new JsonArray((long)Math.Round(segment.Start.TotalMilliseconds / 10), (long)Math.Round(segment.End.TotalMilliseconds / 10)));
            }

            var audioMinutes = samples16kMono.Length / (double)AudioConverter.SampleRate / 60;
            var reply = await _host.RequestAsync(new JsonObject
            {
                ["op"] = "diarize",
                ["pcm"] = temp,
                ["segments"] = ranges,
                ["embedder"] = _embedderPath,
                ["num"] = Math.Max(0, speakers),
                ["max"] = speakers > 0 ? 0 : AutoMaxSpeakers,
            }, TimeSpan.FromSeconds(Math.Max(120, audioMinutes * 60)), cancellationToken).ConfigureAwait(false);

            var labels = reply["speakers"]?.AsArray().Select(n => (int?)n ?? -1).ToList() ?? [];
            return Apply(segments, labels);
        }
        finally
        {
            _host.Stop();
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// يحط رقم المتكلم على كل جملة، ويرقّم المتكلمين بترتيب ظهورهم (أول واحد اتكلم = المتكلم 1).
    /// الجملة اللي معرفناش صاحبها تاخد متكلم الجملة اللي قبلها.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Apply(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<int> labels)
    {
        var order = new Dictionary<int, int>();
        var result = new List<TranscriptSegment>(segments.Count);
        int? previous = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var raw = i < labels.Count ? labels[i] : -1;
            int? speaker = previous;
            if (raw >= 0)
            {
                if (!order.TryGetValue(raw, out var index))
                {
                    index = order.Count;
                    order[raw] = index;
                }

                speaker = index;
            }

            previous = speaker;
            result.Add(segments[i] with { Speaker = speaker });
        }

        return result;
    }

    public void Dispose() => _host.Dispose();
}
