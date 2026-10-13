using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using VoiceType.Speech;

namespace VoiceType.Lab;

/// <summary>قسم "على جهازك": تنزيل نماذج Whisper وتشغيلها محليًا، وتسريع كرت الشاشة.</summary>
public partial class MainWindow
{
    private int _activeDownloads;

    private Border CreateLocalSection()
    {
        var root = new StackPanel();
        var header = new WrapPanel();
        header.Children.Add(new TextBlock { Text = "على جهازك", FontSize = 17, FontWeight = FontWeights.SemiBold });
        header.Children.Add(CreateBadge("مجاني وبدون إنترنت", "OkBrush"));
        root.Children.Add(header);
        root.Children.Add(new TextBlock
        {
            Text = "النموذج يعمل على جهازك والصوت لا يخرج منه. نزّل نموذجًا مرة واحدة ثم استخدمه بلا حدود. ابدأ بالنموذج الموصى به.",
            Foreground = BrushOf("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 10),
        });

        root.Children.Add(CreateGpuRow());

        var installed = LocalModels.Installed();
        foreach (var model in LocalModels.Suggested)
        {
            root.Children.Add(CreateLocalModelRow(model.FileName, model.DisplayName, model.SizeMegabytes, model.Description, model.DownloadUrl, model.Recommended));
        }

        foreach (var file in installed.Where(f => LocalModels.Suggested.All(m => !string.Equals(m.FileName, f, StringComparison.OrdinalIgnoreCase))))
        {
            var size = new FileInfo(LocalModels.PathOf(file)).Length / 1048576.0;
            root.Children.Add(CreateLocalModelRow(file, LocalModels.DisplayNameOf(file), size, "نموذج أضفته بنفسك.", null, false));
        }

        // إضافة نموذج آخر: من ملف أو من رابط
        var addRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var addFile = new Button { Content = "إضافة نموذج من ملف (.bin)…" };
        addFile.Click += OnAddLocalModelFile;
        var openFolder = new Button { Content = "فتح مجلد النماذج" };
        openFolder.Click += (_, _) => OpenFolder(LocalModels.Folder);
        DockPanel.SetDock(addFile, Dock.Left);
        DockPanel.SetDock(openFolder, Dock.Left);
        addRow.Children.Add(addFile);
        addRow.Children.Add(openFolder);

        var urlBox = new TextBox
        {
            FlowDirection = FlowDirection.LeftToRight,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            ToolTip = "رابط مباشر لملف نموذج whisper.cpp ينتهي بـ .bin (مثل رابط Hugging Face)",
        };
        var urlButton = new Button { Content = "تنزيل من رابط" };
        DockPanel.SetDock(urlButton, Dock.Right);
        addRow.Children.Add(urlButton);
        addRow.Children.Add(urlBox);
        root.Children.Add(addRow);

        var urlStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var urlProgress = new ProgressBar { Height = 8, Visibility = Visibility.Collapsed, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(urlProgress);
        root.Children.Add(urlStatus);
        urlButton.Click += async (_, _) =>
        {
            var url = urlBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.AbsolutePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(urlStatus, "الصق رابطًا مباشرًا يبدأ بـ https وينتهي بـ .bin (نموذج بصيغة whisper.cpp).", "WarnBrush");
                return;
            }

            // روابط صفحات Hugging Face (blob) تتحول إلى رابط التنزيل المباشر (resolve)
            var direct = url.Replace("/blob/", "/resolve/", StringComparison.Ordinal);
            await DownloadLocalModelAsync(direct, Path.GetFileName(uri.AbsolutePath), urlProgress, urlStatus, urlButton, null);
        };

        return new Border { Style = (Style)FindResource("Card"), Child = root };
    }

    private StackPanel CreateGpuRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var row = new DockPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        SetStatus(status, "⚙ " + GpuAcceleration.Describe(), "MutedBrush");

        if (GpuAcceleration.IsSupportedPlatform && !GpuAcceleration.IsInstalled)
        {
            var install = new Button { Content = "تسريع بكرت الشاشة (تنزيل 37 MB)" };
            DockPanel.SetDock(install, Dock.Right);
            row.Children.Add(install);
            var progress = new ProgressBar { Height = 8, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

            install.Click += async (_, _) =>
            {
                if (!GpuAcceleration.HasVulkanDriver &&
                    !Confirm("لم يُعثر على دعم Vulkan في تعريف كرت الشاشة، فقد لا يعمل التسريع وسيُستخدم المعالج. هل تريد التنزيل على أي حال؟"))
                {
                    return;
                }

                install.IsEnabled = false;
                progress.Visibility = Visibility.Visible;
                _activeDownloads++;
                try
                {
                    await GpuAcceleration.InstallAsync(new Progress<DownloadProgress>(p =>
                    {
                        progress.IsIndeterminate = p.Fraction is null;
                        progress.Value = (p.Fraction ?? 0) * 100;
                        status.Text = $"جارٍ تنزيل ملفات التسريع… {p}";
                    }));
                    SetStatus(status, "✓ تم تثبيت تسريع كرت الشاشة. أغلق البرنامج وافتحه مرة أخرى ليبدأ استخدامه.", "OkBrush");
                    install.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    SetStatus(status, "✗ تعذّر التنزيل: " + ex.Message, "FailBrush");
                    install.IsEnabled = true;
                }
                finally
                {
                    _activeDownloads--;
                    progress.Visibility = Visibility.Collapsed;
                }
            };

            row.Children.Add(status);
            panel.Children.Add(row);
            panel.Children.Add(progress);
            return panel;
        }

        row.Children.Add(status);
        panel.Children.Add(row);
        return panel;
    }

    private Border CreateLocalModelRow(string fileName, string displayName, double sizeMb, string description, string? url, bool recommended)
    {
        var installed = LocalModels.IsInstalled(fileName);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = displayName, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = $"   {sizeMb:0} MB",
            Foreground = BrushOf("MutedBrush"),
            FlowDirection = FlowDirection.LeftToRight,
        });
        if (recommended)
        {
            title.Children.Add(CreateBadge("موصى به", "AccentBrush"));
        }

        info.Children.Add(title);
        info.Children.Add(new TextBlock { Text = description, Foreground = BrushOf("MutedBrush"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        var progress = new ProgressBar { Height = 8, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var status = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        info.Children.Add(progress);
        info.Children.Add(status);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (installed)
        {
            buttons.Children.Add(new TextBlock { Text = "✓ منزّل", Foreground = BrushOf("OkBrush"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var delete = new Button { Content = "حذف" };
            delete.Click += (_, _) =>
            {
                if (!Confirm($"حذف النموذج \"{displayName}\" من جهازك؟ يمكنك تنزيله مرة أخرى لاحقًا."))
                {
                    return;
                }

                try
                {
                    _ws.UnregisterLocalModel(fileName);
                    LocalModels.Remove(fileName);
                }
                catch (IOException ex)
                {
                    ShowMessage("تعذّر حذف الملف، ربما يستخدمه البرنامج الآن. أغلق البرنامج وافتحه ثم حاول مرة أخرى.\n" + ex.Message, MessageBoxImage.Warning);
                }

                BuildKeysPanel();
                UpdateStatusBar();
            };
            buttons.Children.Add(delete);
        }
        else if (url is not null)
        {
            var download = new Button { Content = "تنزيل", Style = (Style)FindResource("PrimaryButton") };
            var cancel = new Button { Content = "إلغاء", Visibility = Visibility.Collapsed };
            CancellationTokenSource? cts = null;
            download.Click += async (_, _) =>
            {
                cts = new CancellationTokenSource();
                cancel.Visibility = Visibility.Visible;
                await DownloadLocalModelAsync(url, fileName, progress, status, download, cts);
                cancel.Visibility = Visibility.Collapsed;
            };
            cancel.Click += (_, _) => cts?.Cancel();
            buttons.Children.Add(download);
            buttons.Children.Add(cancel);
        }

        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border
        {
            BorderBrush = BrushOf("LineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 2),
            Child = grid,
        };
    }

    private async Task DownloadLocalModelAsync(
        string url,
        string fileName,
        ProgressBar progress,
        TextBlock status,
        Button trigger,
        CancellationTokenSource? cts)
    {
        trigger.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        _activeDownloads++;
        try
        {
            await Downloader.DownloadFileAsync(url, LocalModels.PathOf(fileName), new Progress<DownloadProgress>(p =>
            {
                progress.IsIndeterminate = p.Fraction is null;
                progress.Value = (p.Fraction ?? 0) * 100;
                SetStatus(status, $"جارٍ التنزيل… {p}", "MutedBrush");
            }), cts?.Token ?? CancellationToken.None);

            _ws.RegisterLocalModel(fileName);
            SetStatus(status, "✓ تم التنزيل، وأُضيف النموذج إلى المقارنة.", "OkBrush");
        }
        catch (OperationCanceledException)
        {
            SetStatus(status, "أُلغي التنزيل.", "WarnBrush");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            SetStatus(status, "✗ تعذّر التنزيل. تأكد من الاتصال بالإنترنت وحاول مرة أخرى.  " + ex.Message, "FailBrush");
        }
        finally
        {
            _activeDownloads--;
            trigger.IsEnabled = true;
            progress.Visibility = Visibility.Collapsed;
        }

        if (_activeDownloads == 0 && LocalModels.IsInstalled(fileName))
        {
            BuildKeysPanel();
            UpdateStatusBar();
        }
    }

    private async void OnAddLocalModelFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "اختر ملف نموذج Whisper بصيغة whisper.cpp (.bin)",
            Filter = "نموذج Whisper (*.bin)|*.bin",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            StatusBarText.Text = "جارٍ نسخ النموذج…";
            var fileName = await Task.Run(() => LocalModels.AddFromFile(dialog.FileName));
            _ws.RegisterLocalModel(fileName);
            ShowMessage($"تمت إضافة النموذج \"{fileName}\" وسيظهر في المقارنة.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage(ex.Message, MessageBoxImage.Warning);
        }

        BuildKeysPanel();
        UpdateStatusBar();
    }
}
