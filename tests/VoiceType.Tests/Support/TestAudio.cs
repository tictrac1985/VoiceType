using VoiceType.Core.Speech;

namespace VoiceType.Tests.Support;

public static class TestAudio
{
    /// <summary>ملف WAV صغير صالح (صمت 0.1 ثانية، 16kHz mono).</summary>
    public static AudioPayload Wav()
    {
        const int sampleRate = 16000;
        const int samples = 1600;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + (samples * 2));
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
        writer.Flush();
        return new AudioPayload(stream.ToArray(), "wav", "sample.wav");
    }

    public static AudioPayload Of(string format) => new([1, 2, 3, 4], format);
}
