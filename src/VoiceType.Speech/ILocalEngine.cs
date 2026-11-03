using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// نموذج بيشتغل على جهاز المستخدم (Whisper أو CrispASR): بيتحمّل في الذاكرة مرة واحدة، وبيتفرّغ منها بعد الخمول.
/// </summary>
public interface ILocalEngine : ISpeechEngine, ISegmentedSpeechEngine, IDisposable
{
    /// <summary>يحمّل النموذج مسبقًا (أثناء كلام المستخدم) عشان التفريغ يبدأ على طول.</summary>
    Task PreloadAsync();

    /// <summary>يفرّغ النموذج من الذاكرة لو مش شغال دلوقتي. يرجّع false لو فيه تفريغ جارٍ.</summary>
    bool TryUnload();
}
