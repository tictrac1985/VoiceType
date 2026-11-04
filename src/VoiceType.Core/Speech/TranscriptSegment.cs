namespace VoiceType.Core.Speech;

/// <summary>جزء من التفريغ مع توقيته داخل الملف الصوتي، ورقم المتكلم (0، 1، 2…) لو اتعرف مين اللي قاله.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, int? Speaker = null)
{
    /// <summary>اسم المتكلم للعرض: «المتكلم 1»، «المتكلم 2»…</summary>
    public static string SpeakerName(int speaker) => $"المتكلم {speaker + 1}";
}

/// <summary>محرك يستطيع إرجاع التفريغ مقسمًا بالتوقيتات (مثل Whisper المحلي) — مفيد للملفات الطويلة وترجمات SRT.</summary>
public interface ISegmentedSpeechEngine
{
    Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(
        float[] samples16kMono,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
