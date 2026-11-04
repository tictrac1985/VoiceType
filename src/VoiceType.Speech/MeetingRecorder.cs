using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Dmo;

namespace VoiceType.Speech;

/// <summary>
/// تسجيل اجتماع أو مكالمة: صوت الجهاز (Zoom، Meet، Teams… أي شيء يخرج من السماعات) + صوتك من الميكروفون.
/// يُحفظ كل مصدر في ملف أثناء التسجيل (لا يمتلئ الذاكرة في الاجتماعات الطويلة)، ثم يُدمجان في ملف واحد 16kHz للتفريغ.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private WasapiLoopbackCapture? _system;
    private WasapiCapture? _mic;
    private WaveFileWriter? _systemWriter;
    private WaveFileWriter? _micWriter;
    private long _systemFrames;
    private string? _systemPath;
    private string? _micPath;
    private TaskCompletionSource? _systemStopped;
    private TaskCompletionSource? _micStopped;
    private int _systemRate;
    private int _micRate;
    private long _systemTaken;
    private long _micTaken;

    public bool IsRecording { get; private set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>مستوى الصوت (للعرض): الأعلى بين صوت الجهاز والميكروفون.</summary>
    public double Level { get; private set; }

    public string? FileBase { get; private set; }

    public void Start(string folder, bool includeMic)
    {
        if (IsRecording)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        FileBase = Path.Combine(folder, "اجتماع " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
        _systemPath = FileBase + ".system.wav";
        _micPath = includeMic ? FileBase + ".mic.wav" : null;
        _systemFrames = 0;
        _systemTaken = 0;
        _micTaken = 0;

        try
        {
            _system = new WasapiLoopbackCapture();
            _systemRate = _system.WaveFormat.SampleRate;
            _systemWriter = new WaveFileWriter(_systemPath, new WaveFormat(_systemRate, 16, 1));
            _systemStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _system.DataAvailable += OnSystemData;
            _system.RecordingStopped += (_, _) => _systemStopped?.TrySetResult();

            if (includeMic)
            {
                _mic = new WasapiCapture();
                _micRate = _mic.WaveFormat.SampleRate;
                _micWriter = new WaveFileWriter(_micPath!, new WaveFormat(_micRate, 16, 1));
                _micStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _mic.DataAvailable += OnMicData;
                _mic.RecordingStopped += (_, _) => _micStopped?.TrySetResult();
            }

            _clock.Restart();
            _system.StartRecording();
            _mic?.StartRecording();
            IsRecording = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Cleanup(deleteFiles: true);
            throw new InvalidOperationException("تعذّر تسجيل صوت الجهاز. تأكد أن السماعات أو سماعة الأذن متصلة.", ex);
        }
    }

    /// <summary>يوقف التسجيل ويعيد مسار ملف واحد مدموج (16kHz mono) جاهز للتفريغ.</summary>
    public async Task<string> StopAsync()
    {
        if (!IsRecording)
        {
            throw new InvalidOperationException("لا يوجد تسجيل جارٍ.");
        }

        IsRecording = false;
        _clock.Stop();
        _system?.StopRecording();
        _mic?.StopRecording();
        await Task.WhenAll(
            WaitOrTimeout(_systemStopped?.Task),
            WaitOrTimeout(_micStopped?.Task)).ConfigureAwait(false);

        lock (_gate)
        {
            _systemWriter?.Dispose();
            _systemWriter = null;
            _micWriter?.Dispose();
            _micWriter = null;
        }

        var systemPath = _systemPath!;
        var micPath = _micPath;
        var output = FileBase + ".wav";
        try
        {
            await Task.Run(() =>
            {
                var system = AudioDecoder.DecodeToMono16k(systemPath);
                var mic = micPath is not null && File.Exists(micPath) ? AudioDecoder.DecodeToMono16k(micPath) : [];
                File.WriteAllBytes(output, AudioDecoder.ToWav16k(Mix(system, mic)));
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // لا نحذف التسجيل الأصلي أبدًا إذا فشل الدمج
            Cleanup(deleteFiles: false);
            throw new InvalidOperationException("تعذّر تجهيز التسجيل للتفريغ. الملفات الأصلية محفوظة في: " + Path.GetDirectoryName(systemPath), ex);
        }

        Cleanup(deleteFiles: true);
        return output;
    }

    public void Cancel()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        _clock.Stop();
        try
        {
            _system?.StopRecording();
            _mic?.StopRecording();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }

        lock (_gate)
        {
            _systemWriter?.Dispose();
            _systemWriter = null;
            _micWriter?.Dispose();
            _micWriter = null;
        }

        Cleanup(deleteFiles: true);
    }

    /// <summary>
    /// للعرض أثناء التسجيل: الصوت الجديد من آخر مرة (صوت الجهاز + الميكروفون مدموجين، 16kHz).
    /// بنقرا من الملفات اللي بتتكتب (بعد ما نحدّث رأس الملف)، من غير ما نوقف التسجيل.
    /// المصدرين بياخدوا نفس المدة عشان يفضلوا متزامنين.
    /// </summary>
    public float[] TakeNewAudio16k()
    {
        if (!IsRecording || _systemPath is null)
        {
            return [];
        }

        lock (_gate)
        {
            _systemWriter?.Flush();
            _micWriter?.Flush();
        }

        var systemAvailable = AvailableFrames(_systemPath) - _systemTaken;
        var seconds = systemAvailable / (double)Math.Max(1, _systemRate);
        if (_micPath is not null)
        {
            var micAvailable = AvailableFrames(_micPath) - _micTaken;
            seconds = Math.Min(seconds, micAvailable / (double)Math.Max(1, _micRate));
        }

        if (seconds < 0.5)
        {
            return [];
        }

        var system = Resample(ReadFrames(_systemPath, _systemTaken, (long)(seconds * _systemRate)), _systemRate);
        _systemTaken += (long)(seconds * _systemRate);
        var mic = Array.Empty<float>();
        if (_micPath is not null)
        {
            mic = Resample(ReadFrames(_micPath, _micTaken, (long)(seconds * _micRate)), _micRate);
            _micTaken += (long)(seconds * _micRate);
        }

        return Mix(system, mic);
    }

    /// <summary>عدد العينات المكتوبة في ملف WAV (16-bit mono) لحد دلوقتي.</summary>
    private static long AvailableFrames(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = DataOffset(stream);
            return data < 0 ? 0 : Math.Max(0, (stream.Length - data) / 2);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static float[] ReadFrames(string path, long fromFrame, long count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = DataOffset(stream);
        if (data < 0 || count <= 0)
        {
            return [];
        }

        stream.Position = data + (fromFrame * 2);
        var bytes = new byte[count * 2];
        var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var samples = new float[read / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return samples;
    }

    /// <summary>مكان بداية الصوت في ملف WAV (بعد الرأس)، أو -1.</summary>
    private static long DataOffset(Stream stream)
    {
        var header = new byte[12];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12)
        {
            return -1;
        }

        var chunk = new byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            if (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) < 8)
            {
                return -1;
            }

            var size = BitConverter.ToUInt32(chunk, 4);
            if (chunk[0] == 'd' && chunk[1] == 'a' && chunk[2] == 't' && chunk[3] == 'a')
            {
                return stream.Position;
            }

            stream.Position += size + (size & 1);
        }

        return -1;
    }

    /// <summary>تحويل بسيط لـ 16kHz (كفاية للعرض أثناء التسجيل؛ التفريغ النهائي بيستخدم التحويل الكامل).</summary>
    public static float[] Resample(float[] samples, int rate)
    {
        if (rate == 16000 || samples.Length == 0)
        {
            return samples;
        }

        var ratio = rate / 16000.0;
        var output = new float[(int)(samples.Length / ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * ratio;
            var index = (int)position;
            var next = Math.Min(index + 1, samples.Length - 1);
            var fraction = (float)(position - index);
            output[i] = (samples[index] * (1 - fraction)) + (samples[next] * fraction);
        }

        return output;
    }

    /// <summary>يدمج المصدرين: جمع العينات مع منع التشبع، وطول الناتج = الأطول.</summary>
    public static float[] Mix(float[] a, float[] b)
    {
        if (b.Length == 0)
        {
            return a;
        }

        if (a.Length == 0)
        {
            return b;
        }

        var result = new float[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < result.Length; i++)
        {
            var value = (i < a.Length ? a[i] : 0f) + (i < b.Length ? b[i] : 0f);
            result[i] = Math.Clamp(value, -1f, 1f);
        }

        return result;
    }

    private void OnSystemData(object? sender, WaveInEventArgs e)
    {
        var capture = _system;
        if (capture is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_systemWriter is null)
            {
                return;
            }

            // صوت الجهاز لا يصل أثناء الصمت: نملأ الفراغ بصمت حتى يبقى متزامنًا مع الميكروفون
            var rate = capture.WaveFormat.SampleRate;
            var expected = (long)(_clock.Elapsed.TotalSeconds * rate);
            var incoming = e.BytesRecorded / capture.WaveFormat.BlockAlign;
            var gap = expected - incoming - _systemFrames;
            if (gap > rate / 10)
            {
                var silence = new byte[Math.Min(gap, rate * 10) * 2];
                for (var remaining = gap; remaining > 0; remaining -= silence.Length / 2)
                {
                    _systemWriter.Write(silence, 0, (int)Math.Min(silence.Length, remaining * 2));
                }

                _systemFrames += gap;
            }

            _systemFrames += WriteMono16(_systemWriter, e.Buffer, e.BytesRecorded, capture.WaveFormat, out var peak);
            Level = Math.Max(Level * 0.8, peak);
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        var capture = _mic;
        if (capture is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_micWriter is not null)
            {
                WriteMono16(_micWriter, e.Buffer, e.BytesRecorded, capture.WaveFormat, out var peak);
                Level = Math.Max(Level * 0.8, peak);
            }
        }
    }

    /// <summary>يحوّل أي صيغة من Windows (float/16/24/32-bit، أي عدد قنوات) إلى 16-bit mono.</summary>
    internal static long WriteMono16(WaveFileWriter writer, byte[] buffer, int count, WaveFormat format, out double peak)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var frames = count / (bytesPerSample * channels);
        var output = new byte[frames * 2];
        peak = 0;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var offset = ((f * channels) + c) * bytesPerSample;
                sum += ReadSample(buffer, offset, bytesPerSample, isFloat);
            }

            var value = Math.Clamp(sum / channels, -1.0, 1.0);
            peak = Math.Max(peak, Math.Abs(value));
            var sample = (short)(value * 32767);
            output[f * 2] = (byte)sample;
            output[(f * 2) + 1] = (byte)(sample >> 8);
        }

        writer.Write(output, 0, output.Length);
        return frames;
    }

    private static double ReadSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat) => bytesPerSample switch
    {
        4 when isFloat => BitConverter.ToSingle(buffer, offset),
        4 => BitConverter.ToInt32(buffer, offset) / 2147483648.0,
        3 => ((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24)) / 2147483648.0,
        2 => BitConverter.ToInt16(buffer, offset) / 32768.0,
        _ => 0,
    };

    private static async Task WaitOrTimeout(Task? task)
    {
        if (task is not null)
        {
            await Task.WhenAny(task, Task.Delay(3000)).ConfigureAwait(false);
        }
    }

    private void Cleanup(bool deleteFiles)
    {
        _system?.Dispose();
        _system = null;
        _mic?.Dispose();
        _mic = null;
        if (deleteFiles)
        {
            foreach (var path in new[] { _systemPath, _micPath })
            {
                try
                {
                    if (path is not null && File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        Cancel();
        Cleanup(deleteFiles: false);
    }
}
