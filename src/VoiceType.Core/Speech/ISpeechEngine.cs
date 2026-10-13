namespace VoiceType.Core.Speech;

/// <summary>
/// الواجهة الموحدة لكل محركات التفريغ: API أو محلي.
/// بقية البرنامج لا يعرف مصدر النص.
/// </summary>
public interface ISpeechEngine
{
    /// <summary>معرّف فريد لهذا المحرك (نسخة المزود كما أعدّها المستخدم).</summary>
    string Id { get; }

    string DisplayName { get; }

    SpeechCapabilities Capabilities { get; }

    Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record TranscriptionOptions
{
    /// <summary>رمز اللغة ISO-639-1 مثل "ar". القيمة الفارغة تعني التعرف التلقائي.</summary>
    public string? Language { get; init; } = "ar";

    /// <summary>نص توجيهي (مصطلحات، أسلوب) للمزودات التي تدعمه.</summary>
    public string? Prompt { get; init; }

    public double? Temperature { get; init; }
}

public sealed record SpeechCapabilities
{
    /// <summary>الصيغ المقبولة. null يعني أي صيغة.</summary>
    public IReadOnlyList<string>? Formats { get; init; }

    public double? MaxFileMegabytes { get; init; }

    public bool LanguageRequired { get; init; }

    public bool SupportsPrompt { get; init; }

    public bool RequiresNetwork { get; init; } = true;

    public bool AcceptsFormat(string format) =>
        Formats is null || Formats.Count == 0 ||
        Formats.Contains(AudioFormats.Normalize(format), StringComparer.OrdinalIgnoreCase);
}

public sealed record TranscriptionResult
{
    public required string Text { get; init; }

    public required string EngineId { get; init; }

    public string? Model { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>التكلفة إن أعادها المزود (مثل OpenRouter).</summary>
    public double? Cost { get; init; }

    /// <summary>المحركات التي فشلت قبل نجاح هذا المحرك (سلسلة البدائل).</summary>
    public IReadOnlyList<EngineFailure> PreviousFailures { get; init; } = [];
}

/// <summary>محرك فشل قبل النجاح. Details تفاصيل تقنية للسجل (بدون مفاتيح أو صوت).</summary>
public sealed record EngineFailure(string EngineId, SpeechErrorKind Kind, string Message, string? Details = null);
