namespace VoiceType.Core.Speech;

/// <summary>مقاطع صوتية مولّدة للاختبار (مثل زر "اختبار المفتاح").</summary>
public static class SampleAudio
{
    /// <summary>ملف WAV صامت 16kHz mono.</summary>
    public static AudioPayload Silence(double seconds = 1.0)
    {
        const int sampleRate = 16000;
        var samples = (int)(sampleRate * seconds);
        var dataBytes = samples * 2;

        using var stream = new MemoryStream(44 + dataBytes);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            writer.Write(new byte[dataBytes]);
        }

        return new AudioPayload(stream.ToArray(), "wav", "test.wav");
    }
}
