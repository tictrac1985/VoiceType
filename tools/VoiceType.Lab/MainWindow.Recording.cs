using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using VoiceType.Settings;

namespace VoiceType.Lab;

public partial class MainWindow
{
    private static readonly WaveFormat RecordingFormat = new(16000, 16, 1);
    private readonly List<SentenceItem> _sentences = [];
    private readonly object _writerGate = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private SentenceItem? _recordingItem;
    private string? _recordingTemp;
    private DateTime _recordStart;
    private bool _isRecording;

    private WaveOutEvent? _player;
    private WaveFileReader? _playerReader;

    private string AudioFolder => _ws.Settings.ResolvedAudioFolder;

    private void InitializeRecording()
    {
        MicCombo.Items.Clear();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            MicCombo.Items.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }

        if (MicCombo.Items.Count == 0)
        {
            MicCombo.Items.Add("لم يُعثر على ميكروفون");
            RecordButton.IsEnabled = false;
        }

        MicCombo.SelectedIndex = 0;
    }

    private void LoadSentences()
    {
        if (_isRecording)
        {
            return;
        }

        var selectedIndex = (SentenceList.SelectedItem as SentenceItem)?.Index ?? 1;
        _sentences.Clear();

        var existing = Directory.Exists(AudioFolder)
            ? Directory.EnumerateFiles(AudioFolder)
                .Select(f => Regex.Match(Path.GetFileNameWithoutExtension(f), @"^(\d{1,3})$"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToList()
            : [];
        var count = Math.Max(Sentences.Defaults.Count, existing.Count == 0 ? 0 : existing.Max());

        for (var i = 1; i <= count; i++)
        {
            var item = new SentenceItem { Index = i };
            var textFile = TextPath(item);
            item.Text = File.Exists(textFile)
                ? File.ReadAllText(textFile).Trim()
                : i <= Sentences.Defaults.Count ? Sentences.Defaults[i - 1] : string.Empty;
            item.Recorded = File.Exists(WavPath(item));
            _sentences.Add(item);
        }

        SentenceList.ItemsSource = null;
        SentenceList.ItemsSource = _sentences;
        SentenceList.SelectedItem = _sentences.FirstOrDefault(s => s.Index == selectedIndex) ?? _sentences.FirstOrDefault();
    }

    private string WavPath(SentenceItem item) => Path.Combine(AudioFolder, item.FileBase + ".wav");

    private string TextPath(SentenceItem item) => Path.Combine(AudioFolder, item.FileBase + ".txt");

    private void OnSentenceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SentenceList.SelectedItem is not SentenceItem item)
        {
            return;
        }

        SentenceTitle.Text = $"الجملة {item.Index} من {_sentences.Count}" + (item.Recorded ? "  —  ✓ مسجلة" : string.Empty);
        SentenceText.Text = item.Text;
        PlayButton.IsEnabled = item.Recorded;
        RecordStatus.Text = item.Recorded ? "هذه الجملة مسجلة. يمكنك الاستماع إليها أو إعادة تسجيلها." : string.Empty;
        RecordStatus.Foreground = BrushOf("MutedBrush");
    }

    private void OnSentenceTextLostFocus(object sender, RoutedEventArgs e) => SaveCurrentText();

    /// <summary>يحفظ النص الصحيح للجملة الحالية (إذا كانت مسجلة) حتى يُستخدم في حساب الدقة.</summary>
    private void SaveCurrentText()
    {
        if (SentenceList.SelectedItem is not SentenceItem item)
        {
            return;
        }

        var text = SentenceText.Text.Trim();
        if (text == item.Text)
        {
            return;
        }

        item.Text = text;
        if (item.Recorded)
        {
            File.WriteAllText(TextPath(item), text, new UTF8Encoding(false));
        }

        SentenceList.Items.Refresh();
    }

    private void OnAddSentence(object sender, RoutedEventArgs e)
    {
        var item = new SentenceItem { Index = _sentences.Count == 0 ? 1 : _sentences.Max(s => s.Index) + 1 };
        _sentences.Add(item);
        SentenceList.Items.Refresh();
        SentenceList.SelectedItem = item;
        SentenceList.ScrollIntoView(item);
        SentenceText.Focus();
        RecordStatus.Text = "اكتب الجملة التي ستقولها، ثم اضغط \"تسجيل\".";
    }

    private void OnNextSentence(object sender, RoutedEventArgs e)
    {
        SaveCurrentText();
        var index = SentenceList.SelectedIndex;
        if (index < _sentences.Count - 1)
        {
            SentenceList.SelectedIndex = index + 1;
            SentenceList.ScrollIntoView(SentenceList.SelectedItem);
        }
        else
        {
            RecordStatus.Text = "هذه آخر جملة. يمكنك إضافة جملة جديدة، أو الانتقال إلى خطوة المقارنة.";
        }
    }

    private void OnOpenAudioFolder(object sender, RoutedEventArgs e) => OpenFolder(AudioFolder);

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            _waveIn?.StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        if (SentenceList.SelectedItem is not SentenceItem item)
        {
            RecordStatus.Text = "اختر جملة من القائمة أولًا.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SentenceText.Text))
        {
            RecordStatus.Text = "اكتب نص الجملة أولًا، حتى نعرف الكلام الصحيح عند المقارنة.";
            return;
        }

        StopPlayback();
        SaveCurrentText();
        Directory.CreateDirectory(AudioFolder);

        _recordingItem = item;
        _recordingTemp = Path.Combine(AudioFolder, item.FileBase + ".recording.tmp");

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Max(0, MicCombo.SelectedIndex),
                WaveFormat = RecordingFormat,
                BufferMilliseconds = 50,
            };
            _writer = new WaveFileWriter(_recordingTemp, RecordingFormat);
            _waveIn.DataAvailable += OnAudioData;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
        }
        catch (Exception ex) when (ex is NAudio.MmException or IOException or UnauthorizedAccessException)
        {
            CleanupRecording();
            RecordStatus.Text = "تعذّر تشغيل الميكروفون. تأكد أنه متصل، وأن Windows يسمح للتطبيقات باستخدامه (الإعدادات ← الخصوصية ← الميكروفون).\n" + ex.Message;
            RecordStatus.Foreground = BrushOf("FailBrush");
            return;
        }

        _isRecording = true;
        _recordStart = DateTime.Now;
        RecordButton.Content = "■  إيقاف";
        SentenceList.IsEnabled = NextButton.IsEnabled = PlayButton.IsEnabled = MicCombo.IsEnabled = SentenceText.IsEnabled = false;
        RecordStatus.Text = "🎙 جارٍ التسجيل… اقرأ الجملة الآن، ثم اضغط \"إيقاف\".";
        RecordStatus.Foreground = BrushOf("FailBrush");
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        lock (_writerGate)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
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
        Dispatcher.InvokeAsync(() => LevelBar.Value = level);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => FinishRecording(e.Exception));
    }

    private void FinishRecording(Exception? error)
    {
        var item = _recordingItem;
        var temp = _recordingTemp;
        var duration = DateTime.Now - _recordStart;
        _isRecording = false;
        CleanupRecording();

        RecordButton.Content = "●  تسجيل";
        SentenceList.IsEnabled = NextButton.IsEnabled = MicCombo.IsEnabled = SentenceText.IsEnabled = true;
        LevelBar.Value = 0;

        if (item is null || temp is null || !File.Exists(temp))
        {
            return;
        }

        if (error is not null)
        {
            TryDelete(temp);
            RecordStatus.Text = "انقطع التسجيل: " + error.Message;
            RecordStatus.Foreground = BrushOf("FailBrush");
            return;
        }

        if (duration < TimeSpan.FromSeconds(0.8))
        {
            TryDelete(temp);
            RecordStatus.Text = "التسجيل قصير جدًا. اضغط \"تسجيل\" ثم اقرأ الجملة كاملة.";
            RecordStatus.Foreground = BrushOf("WarnBrush");
            return;
        }

        File.Move(temp, WavPath(item), overwrite: true);
        File.WriteAllText(TextPath(item), item.Text, new UTF8Encoding(false));
        item.Recorded = true;
        SentenceList.Items.Refresh();
        PlayButton.IsEnabled = true;
        SentenceTitle.Text = $"الجملة {item.Index} من {_sentences.Count}  —  ✓ مسجلة";
        RecordStatus.Text = $"✓ تم حفظ التسجيل ({duration.TotalSeconds:0.0} ثانية). اضغط \"الجملة التالية\".";
        RecordStatus.Foreground = BrushOf("OkBrush");
        UpdateStatusBar();
    }

    private void CleanupRecording()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnAudioData;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            if (_isRecording)
            {
                try
                {
                    _waveIn.StopRecording();
                }
                catch (NAudio.MmException)
                {
                    // الجهاز فُصل أثناء التسجيل
                }
            }

            _waveIn.Dispose();
            _waveIn = null;
        }

        lock (_writerGate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        StopPlayback();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
        {
            StopPlayback();
            return;
        }

        if (SentenceList.SelectedItem is not SentenceItem item || !File.Exists(WavPath(item)))
        {
            return;
        }

        try
        {
            _playerReader = new WaveFileReader(WavPath(item));
            _player = new WaveOutEvent();
            _player.Init(_playerReader);
            _player.PlaybackStopped += (_, _) => Dispatcher.InvokeAsync(StopPlayback);
            _player.Play();
            PlayButton.Content = "■  إيقاف الاستماع";
        }
        catch (Exception ex) when (ex is NAudio.MmException or IOException or FormatException)
        {
            StopPlayback();
            RecordStatus.Text = "تعذّر تشغيل التسجيل: " + ex.Message;
        }
    }

    private void StopPlayback()
    {
        var player = _player;
        _player = null;
        player?.Dispose();
        _playerReader?.Dispose();
        _playerReader = null;
        PlayButton.Content = "▶  استماع";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
