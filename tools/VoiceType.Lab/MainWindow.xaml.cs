using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoiceType.Evaluation.Comparison;
using VoiceType.Settings;

namespace VoiceType.Lab;

/// <summary>
/// النافذة الرئيسية. مقسمة على عدة ملفات:
/// MainWindow.Keys.cs (المفاتيح) — MainWindow.Recording.cs (التسجيل) — MainWindow.Compare.cs (المقارنة).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private LabWorkspace _ws = null!;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _ws = LabWorkspace.Open();
        }
        catch (Exception ex)
        {
            ShowMessage("تعذّر فتح إعدادات البرنامج:\n" + ex.Message, MessageBoxImage.Error);
            Close();
            return;
        }

        InitializeRecording();
        InitializeCompare();
        BuildKeysPanel();
        LoadSentences();
        BuildModelsPanel();
        UpdateSamplesInfo();
        UpdateStatusBar();

        // نفتح الخطوة التي يحتاجها المستخدم الآن
        Tabs.SelectedIndex = _ws.ActiveTargets().Count == 0 ? 0 : CountRecordings() == 0 ? 1 : 2;
        _initializing = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CleanupRecording();
        _compareCts?.Cancel();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged يصل أيضًا من القوائم داخل التبويبات، فنتجاهلها
        if (!ReferenceEquals(e.OriginalSource, Tabs) || _initializing)
        {
            return;
        }

        switch (Tabs.SelectedIndex)
        {
            case 0 when _activeDownloads == 0:
                BuildKeysPanel();
                break;
            case 1:
                LoadSentences();
                break;
            case 2:
                BuildModelsPanel();
                UpdateSamplesInfo();
                break;
        }

        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var keys = _ws.KeyProviders.Count(d => _ws.HasKey(d.Id));
        StatusBarText.Text =
            $"نماذج على جهازك: {VoiceType.Speech.LocalModels.Installed().Count}   ·   مفاتيح محفوظة: {keys}   ·   نماذج جاهزة للمقارنة: {_ws.ActiveTargets().Count}   ·   تسجيلات: {CountRecordings()}";
    }

    private int CountRecordings() => ComparisonRunner.LoadSamples(_ws.Settings.ResolvedAudioFolder).Count;

    internal static void OpenInShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowMessage("تعذّر الفتح:\n" + target, MessageBoxImage.Warning);
        }
    }

    internal static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenInShell(folder);
    }

    internal static void ShowMessage(string text, MessageBoxImage icon = MessageBoxImage.Information) =>
        MessageBox.Show(text, "VoiceType Lab", MessageBoxButton.OK, icon, MessageBoxResult.OK,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    internal static bool Confirm(string text) =>
        MessageBox.Show(text, "VoiceType Lab", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == MessageBoxResult.Yes;

    private Brush BrushOf(string key) => (Brush)FindResource(key);
}
