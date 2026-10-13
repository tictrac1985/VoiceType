namespace VoiceType.Core.Speech;

/// <summary>جزء من التفريغ مع توقيته داخل الملف الصوتي.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>محرك يستطيع إرجاع التفريغ مقسمًا بالتوقيتات (مثل Whisper المحلي) — مفيد للملفات الطويلة وترجمات SRT.</summary>
public interface ISegmentedSpeechEngine
{
    Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(
        float[] samples16kMono,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
