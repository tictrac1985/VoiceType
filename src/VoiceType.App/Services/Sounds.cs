using System.IO;
using NAudio.Wave;

namespace VoiceType.App.Services;

/// <summary>نغمات قصيرة خفيفة عند بدء التسجيل وإيقافه (يمكن إيقافها من الإعدادات).</summary>
public static class Sounds
{
    private static readonly byte[] StartTone = Tone([660, 880], 70);
    private static readonly byte[] StopTone = Tone([880, 660], 70);
    private static readonly byte[] ErrorTone = Tone([300, 240], 110);

    public static bool Enabled { get; set; } = true;

    public static void Start() => Play(StartTone);

    public static void Stop() => Play(StopTone);

    public static void Error() => Play(ErrorTone);

    private static void Play(byte[] wav)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += (_, _) =>
            {
                output.Dispose();
                reader.Dispose();
            };
            output.Play();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            // لا يوجد جهاز صوت: نتجاهل
        }
    }

    private static byte[] Tone(int[] frequencies, int millisecondsEach)
    {
        const int rate = 22050;
        var samplesEach = rate * millisecondsEach / 1000;
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), new WaveFormat(rate, 16, 1)))
        {
            foreach (var frequency in frequencies)
            {
                for (var i = 0; i < samplesEach; i++)
                {
                    var envelope = Math.Min(1.0, Math.Min(i, samplesEach - i) / (rate * 0.01));
                    var value = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 0.18 * envelope * short.MaxValue);
                    writer.WriteSample(value / (float)short.MaxValue);
                }
            }
        }

        return stream.ToArray();
    }
}
