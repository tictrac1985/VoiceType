using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoiceType.Evaluation.Comparison;
using VoiceType.Providers.Definitions;
using VoiceType.Settings;

namespace VoiceType.Lab;

public partial class MainWindow
{
    private CancellationTokenSource? _compareCts;
    private string? _lastReport;
    private string? _lastResultsFolder;

    private void InitializeCompare()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "عربي (مع كلمات إنجليزية)", Tag = "ar" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "تلقائي", Tag = "" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "إنجليزي", Tag = "en" });
        var current = _ws.Settings.Language ?? string.Empty;
        LanguageCombo.SelectedItem = LanguageCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                                     ?? LanguageCombo.Items[0];
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = (string)item.Tag;
        _ws.Settings.Language = tag.Length == 0 ? null : tag;
        _ws.Settings.Save();
    }

    private void BuildModelsPanel()
    {
        ModelsPanel.Children.Clear();
        AddProviderCombo.Items.Clear();

        // النماذج المحلية على الجهاز أولًا
        var localModels = _ws.Settings.Models.Where(m => LabWorkspace.IsLocal(m.Provider)).ToList();
        ModelsPanel.Children.Add(new TextBlock
        {
            Text = "على جهازك (مجاني)" + (localModels.Count == 0 ? "   — نزّل نموذجًا من الخطوة ١" : string.Empty),
            FontWeight = FontWeights.SemiBold,
            Foreground = localModels.Count > 0 ? BrushOf("OkBrush") : BrushOf("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 2),
        });
        foreach (var model in localModels)
        {
            ModelsPanel.Children.Add(CreateModelRow(model, _ws.IsReady(model)));
        }

        foreach (var definition in _ws.Catalog.All
                     .Where(d => d.Id != "openai-compatible" || _ws.Settings.Models.Any(m => m.Provider == d.Id))
                     .OrderBy(LabWorkspace.PricingRank)
                     .ThenBy(d => d.Name))
        {
            var models = _ws.Settings.Models
                .Where(m => string.Equals(m.Provider, definition.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var needsKey = LabWorkspace.NeedsKey(definition);
            var hasKey = !needsKey || _ws.HasKey(definition.Id);

            AddProviderCombo.Items.Add(new ComboBoxItem { Content = definition.Name, Tag = definition.Id });

            if (models.Count == 0)
            {
                continue;
            }

            ModelsPanel.Children.Add(new TextBlock
            {
                Text = $"{definition.Name}  ({definition.PricingLabel})" + (hasKey ? string.Empty : "   — أضف المفتاح في الخطوة ١"),
                FontWeight = FontWeights.SemiBold,
                Foreground = hasKey ? BrushOf("AccentBrush") : BrushOf("MutedBrush"),
                Margin = new Thickness(0, 10, 0, 2),
            });

            foreach (var model in models)
            {
                ModelsPanel.Children.Add(CreateModelRow(model, _ws.IsReady(model)));
            }
        }

        if (AddProviderCombo.Items.Count > 0)
        {
            AddProviderCombo.SelectedIndex = 0;
        }
    }

    private DockPanel CreateModelRow(LabModel model, bool ready)
    {
        var row = new DockPanel { Margin = new Thickness(8, 1, 0, 1) };

        var remove = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, ToolTip = "إزالة من القائمة" };
        remove.Click += (_, _) =>
        {
            _ws.Settings.Remove(model.Provider, model.Model);
            _ws.Settings.Save();
            BuildModelsPanel();
            UpdateStatusBar();
        };
        DockPanel.SetDock(remove, Dock.Left);
        row.Children.Add(remove);

        var check = new CheckBox
        {
            IsChecked = model.Enabled,
            IsEnabled = ready,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = LabWorkspace.IsLocal(model.Provider) ? VoiceType.Speech.LocalModels.DisplayNameOf(model.Model ?? string.Empty) : model.Model ?? "(افتراضي)",
                FlowDirection = LabWorkspace.IsLocal(model.Provider) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            },
        };
        check.Checked += (_, _) => SetModelEnabled(model, true);
        check.Unchecked += (_, _) => SetModelEnabled(model, false);
        row.Children.Add(check);
        return row;
    }

    private void SetModelEnabled(LabModel model, bool enabled)
    {
        model.Enabled = enabled;
        _ws.Settings.Save();
        UpdateStatusBar();
    }

    private void OnAddModel(object sender, RoutedEventArgs e)
    {
        if (AddProviderCombo.SelectedItem is not ComboBoxItem provider)
        {
            return;
        }

        var modelId = AddModelText.Text.Trim();
        if (modelId.Length == 0)
        {
            ShowMessage("اكتب معرّف النموذج كما يظهر في موقع الخدمة.\nمثال لـ OpenRouter:  openai/whisper-large-v3");
            return;
        }

        _ws.Settings.Add((string)provider.Tag, modelId);
        _ws.Settings.Save();
        AddModelText.Clear();
        BuildModelsPanel();
        UpdateStatusBar();
    }

    private void UpdateSamplesInfo()
    {
        var samples = ComparisonRunner.LoadSamples(_ws.Settings.ResolvedAudioFolder);
        SamplesInfo.Text = samples.Count == 0
            ? "لا توجد تسجيلات بعد. سجّل بعض الجمل في الخطوة ٢ أولًا."
            : $"التسجيلات: {samples.Count} (منها {samples.Count(s => s.Reference is not null)} بنص صحيح)   —   المجلد: {_ws.Settings.ResolvedAudioFolder}";
    }

    private void OnChooseAudioFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "اختر مجلد التسجيلات (ملفات صوتية وبجوار كل ملف نصه الصحيح بنفس الاسم .txt)",
            InitialDirectory = Directory.Exists(_ws.Settings.ResolvedAudioFolder) ? _ws.Settings.ResolvedAudioFolder : string.Empty,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _ws.Settings.AudioFolder = string.Equals(dialog.FolderName, AppPaths.LabAudio, StringComparison.OrdinalIgnoreCase)
            ? null
            : dialog.FolderName;
        _ws.Settings.Save();
        UpdateSamplesInfo();
        LoadSentences();
        UpdateStatusBar();
    }

    private async void OnRunCompare(object sender, RoutedEventArgs e)
    {
        var samples = ComparisonRunner.LoadSamples(_ws.Settings.ResolvedAudioFolder);
        if (samples.Count == 0)
        {
            ShowMessage("لا توجد تسجيلات. سجّل بعض الجمل في الخطوة ٢ أولًا.");
            Tabs.SelectedIndex = 1;
            return;
        }

        var targets = _ws.ActiveTargets();
        if (targets.Count == 0)
        {
            ShowMessage("لا يوجد نموذج جاهز. أدخل مفتاحًا واحدًا على الأقل في الخطوة ١، وتأكد أن نموذجًا واحدًا على الأقل مُعلَّم.");
            return;
        }

        var requests = samples.Count * targets.Count;
        if (requests > 80 && !Confirm($"سيتم إرسال {requests} طلبًا ({samples.Count} تسجيل × {targets.Count} نموذج). قد يستهلك هذا من رصيدك لدى الخدمات. هل تريد المتابعة؟"))
        {
            return;
        }

        _compareCts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        OpenReportButton.IsEnabled = false;
        ResultsGrid.ItemsSource = null;
        CompareProgressBar.Maximum = requests;
        CompareProgressBar.Value = 0;
        CompareStatus.Text = $"جارٍ المقارنة: {samples.Count} تسجيل × {targets.Count} نموذج…";
        CompareStatus.Foreground = BrushOf("MutedBrush");

        var progress = new Progress<CompareProgress>(p =>
        {
            CompareProgressBar.Value = p.Completed;
            CompareStatus.Text = $"{p.Completed} / {p.Total}   —   {p.Label}   —   {p.FileName}   {(p.Success ? "✓" : "✗")}";
        });

        try
        {
            var runner = _ws.CreateRunner(Http);
            var results = await runner.RunAsync(samples, targets, _ws.TranscriptionOptions, _ws.Settings.Parallelism, progress, _compareCts.Token);

            _lastResultsFolder = _ws.NewResultsFolder();
            ReportWriter.Write(_lastResultsFolder, results, _ws.Settings.Language);
            _lastReport = Path.Combine(_lastResultsFolder, "report.html");
            OpenReportButton.IsEnabled = true;

            ShowResults(results, samples.Any(s => s.Reference is not null));
        }
        catch (OperationCanceledException)
        {
            CompareStatus.Text = "أُوقفت المقارنة.";
            CompareStatus.Foreground = BrushOf("WarnBrush");
        }
        finally
        {
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _compareCts?.Dispose();
            _compareCts = null;
        }
    }

    private void ShowResults(IReadOnlyList<FileResult> results, bool hasReferences)
    {
        var summaries = ReportWriter.Summarize(results).Where(s => s.SkipReason is null).ToList();
        var rank = 1;
        ResultsGrid.ItemsSource = summaries.Select(s => new ResultRow
        {
            Rank = rank++,
            Label = s.Label,
            Wer = Percent(s.AverageWer),
            Latin = Percent(s.LatinAccuracy),
            Latency = s.AverageLatency is { } l ? l.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " ث" : "—",
            OkFail = $"{s.Succeeded} / {s.Failed}",
            Cost = s.TotalCost is { } c ? "$" + c.ToString("0.0000", CultureInfo.InvariantCulture) : "—",
        }).ToList();

        var best = summaries.FirstOrDefault(s => s.AverageWer is not null);
        var failures = summaries.Sum(s => s.Failed);
        if (!hasReferences)
        {
            CompareStatus.Text = "انتهت المقارنة. لا توجد نصوص صحيحة للتسجيلات، لذلك لا يمكن حساب الدقة. افتح التقرير لقراءة النصوص ومقارنتها بنفسك.";
            CompareStatus.Foreground = BrushOf("WarnBrush");
        }
        else if (best is not null)
        {
            CompareStatus.Text = $"✓ انتهت المقارنة. الأدق للهجتك: {best.Label}  (نسبة الخطأ {Percent(best.AverageWer)})." +
                                 (failures > 0 ? $"   ملاحظة: فشل {failures} طلب، التفاصيل في التقرير." : string.Empty);
            CompareStatus.Foreground = BrushOf("OkBrush");
        }
        else
        {
            CompareStatus.Text = "انتهت المقارنة لكن كل الطلبات فشلت. افتح التقرير لمعرفة السبب (غالبًا المفتاح أو الرصيد).";
            CompareStatus.Foreground = BrushOf("FailBrush");
        }
    }

    private void OnCancelCompare(object sender, RoutedEventArgs e) => _compareCts?.Cancel();

    private void OnOpenReport(object sender, RoutedEventArgs e)
    {
        if (_lastReport is not null && File.Exists(_lastReport))
        {
            OpenInShell(_lastReport);
        }
    }

    private void OnOpenResultsFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(_lastResultsFolder ?? AppPaths.LabResults);

    private static string Percent(double? value) =>
        value is { } v ? (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
}

/// <summary>صف في جدول النتائج (public حتى يعمل الربط Binding في WPF).</summary>
public sealed class ResultRow
{
    public int Rank { get; init; }

    public string Label { get; init; } = string.Empty;

    public string Wer { get; init; } = string.Empty;

    public string Latin { get; init; } = string.Empty;

    public string Latency { get; init; } = string.Empty;

    public string OkFail { get; init; } = string.Empty;

    public string Cost { get; init; } = string.Empty;
}
