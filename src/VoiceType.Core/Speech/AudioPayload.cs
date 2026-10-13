namespace VoiceType.Core.Speech;

/// <summary>
/// مقطع صوتي جاهز للإرسال إلى محرك تفريغ.
/// </summary>
public sealed class AudioPayload
{
    public AudioPayload(byte[] data, string format, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new ArgumentException("Audio data is empty.", nameof(data));
        }

        Data = data;
        Format = AudioFormats.Normalize(format);
        FileName = string.IsNullOrWhiteSpace(fileName) ? $"audio.{Format}" : fileName;
    }

    public byte[] Data { get; }

    /// <summary>الصيغة بعد التوحيد، مثل wav أو mp3.</summary>
    public string Format { get; }

    public string FileName { get; }

    public string MimeType => AudioFormats.GetMimeType(Format);

    public double SizeInMegabytes => Data.Length / (1024d * 1024d);

    public static AudioPayload FromFile(string path) =>
        new(File.ReadAllBytes(path), AudioFormats.FromExtension(path), Path.GetFileName(path));
}

public static class AudioFormats
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wav"] = "audio/wav",
        ["mp3"] = "audio/mpeg",
        ["flac"] = "audio/flac",
        ["ogg"] = "audio/ogg",
        ["opus"] = "audio/ogg",
        ["m4a"] = "audio/mp4",
        ["mp4"] = "audio/mp4",
        ["webm"] = "audio/webm",
        ["aac"] = "audio/aac",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mpeg"] = "mp3",
        ["mpga"] = "mp3",
        ["wave"] = "wav",
        ["oga"] = "ogg",
    };

    public static IReadOnlyCollection<string> Known => MimeTypes.Keys;

    public static bool TryNormalize(string? format, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        var value = format.Trim().TrimStart('.').ToLowerInvariant();
        if (Aliases.TryGetValue(value, out var alias))
        {
            value = alias;
        }

        if (!MimeTypes.ContainsKey(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    public static string Normalize(string format) =>
        TryNormalize(format, out var normalized)
            ? normalized
            : throw new ArgumentException($"Unsupported audio format '{format}'.", nameof(format));

    public static string FromExtension(string path) => Normalize(Path.GetExtension(path));

    public static bool IsAudioFile(string path) => TryNormalize(Path.GetExtension(path), out _);

    public static string GetMimeType(string format) => MimeTypes[Normalize(format)];
}
