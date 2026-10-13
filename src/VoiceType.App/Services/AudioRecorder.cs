using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace VoiceType.App.Services;

/// <summary>تسجيل الميكروفون بصيغة 16kHz mono WAV في الذاكرة.</summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly object _gate = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private MemoryStream? _buffer;
    private TaskCompletionSource<byte[]>? _stopped;
    private double _peakSum;
    private int _peakCount;

    public event Action<double>? LevelChanged;

    /// <summary>صوت PCM خام (16kHz/16bit/mono) أولًا بأول، للتفريغ المباشر. يُستدعى من خيط الصوت.</summary>
    public event Action<byte[], int>? Pcm;

    /// <summary>توقف التسجيل فجأة (مثل فصل سماعة البلوتوث).</summary>
    public event Action<Exception>? Failed;

    public bool IsRecording { get; private set; }

    public DateTime StartedAt { get; private set; }

    /// <summary>متوسط مستوى الصوت أثناء التسجيل (لاكتشاف الصمت).</summary>
    public double AverageLevel => _peakCount == 0 ? 0 : _peakSum / _peakCount;

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }

        return list;
    }

    public void Start(int deviceNumber = -1)
    {
        if (IsRecording)
        {
            return;
        }

        if (_waveIn is not null)
        {
            Cleanup(); // تسجيل سابق ما زال يتوقف
        }

        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("لم يُعثر على ميكروفون. وصّل ميكروفونًا وحاول مرة أخرى.");
        }

        _buffer = new MemoryStream();
        var format = new WaveFormat(16000, 16, 1);
        _writer = new WaveFileWriter(new IgnoreDisposeStream(_buffer), format);
        _stopped = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _peakSum = 0;
        _peakCount = 0;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber < WaveInEvent.DeviceCount ? deviceNumber : -1,
            WaveFormat = format,
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += OnStopped;
        try
        {
            _waveIn.StartRecording();
        }
        catch (NAudio.MmException ex)
        {
            Cleanup();
            throw new InvalidOperationException(
                "تعذّر تشغيل الميكروفون. تأكد أنه متصل، وأن Windows يسمح للتطبيقات باستخدامه (الإعدادات ← الخصوصية ← الميكروفون).", ex);
        }

        IsRecording = true;
        StartedAt = DateTime.Now;
    }

    /// <summary>يوقف التسجيل ويعيد ملف WAV كاملًا.</summary>
    public Task<byte[]> StopAsync()
    {
        if (!IsRecording || _waveIn is null || _stopped is null)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        IsRecording = false;
        var task = _stopped.Task;
        try
        {
            _waveIn.StopRecording();
        }
        catch (NAudio.MmException)
        {
            OnStopped(_waveIn, new StoppedEventArgs());
        }

        return task;
    }

    public void Cancel()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        var completion = _stopped;
        Cleanup(); // يفصل المعالجات ويوقف الميكروفون فورًا، فلا يؤثر إيقاف متأخر على التسجيل التالي
        completion?.TrySetCanceled();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (!ReferenceEquals(sender, _waveIn))
        {
            return;
        }

        lock (_gate)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        try
        {
            Pcm?.Invoke(e.Buffer, e.BytesRecorded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // التفريغ المباشر لا يجب أن يوقف التسجيل أبدًا
        }

        var peak = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            var sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        var level = peak / 32768.0;
        _peakSum += level;
        _peakCount++;
        LevelChanged?.Invoke(level);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _waveIn))
        {
            return; // من تسجيل قديم أُلغي
        }

        var unexpected = IsRecording;
        IsRecording = false;
        byte[] data;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            data = _buffer?.ToArray() ?? [];
        }

        var completion = _stopped;
        Cleanup();
        if (e.Exception is not null)
        {
            completion?.TrySetException(e.Exception);
            if (unexpected)
            {
                Failed?.Invoke(e.Exception);
            }
        }
        else
        {
            completion?.TrySetResult(data);
        }
    }

    private void Cleanup()
    {
        if (_waveIn is not null)
        {
            var waveIn = _waveIn;
            _waveIn = null;
            waveIn.DataAvailable -= OnData;
            waveIn.RecordingStopped -= OnStopped;
            try
            {
                waveIn.Dispose();
            }
            catch (NAudio.MmException)
            {
            }
        }

        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        _buffer = null;
    }

    public void Dispose()
    {
        Cancel();
        Cleanup();
    }
}
