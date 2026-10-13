using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Settings;

namespace VoiceType.Lab;

public partial class MainWindow
{
    private void BuildKeysPanel()
    {
        KeysPanel.Children.Clear();

        if (_ws.Keys.Unreadable.Count > 0)
        {
            KeysPanel.Children.Add(new TextBlock
            {
                Text = "بعض المفاتيح حُفظت على جهاز أو حساب آخر ولا يمكن قراءتها هنا. أدخلها مرة أخرى.",
                Foreground = BrushOf("WarnBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        KeysPanel.Children.Add(CreateLocalSection());
        KeysPanel.Children.Add(new TextBlock
        {
            Text = "خدمات عبر الإنترنت (تحتاج مفتاحًا)",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 8),
        });

        foreach (var definition in _ws.KeyProviders)
        {
            KeysPanel.Children.Add(CreateKeyRow(definition));
        }

        foreach (var error in _ws.Catalog.LoadErrors)
        {
            KeysPanel.Children.Add(new TextBlock
            {
                Text = "ملف خدمة مخصصة غير صالح: " + error,
                Foreground = BrushOf("FailBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private Border CreateKeyRow(ProviderDefinition definition)
    {
        var root = new StackPanel();

        // السطر الأول: الاسم + رابط الحصول على مفتاح
        var header = new WrapPanel();
        header.Children.Add(new TextBlock { Text = definition.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
        header.Children.Add(CreateBadge(definition.PricingLabel, definition.IsPaid ? "MutedBrush" : definition.Pricing == "trial" ? "AccentBrush" : "OkBrush"));
        var url = definition.KeyUrl ?? definition.Website;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var link = new Hyperlink(new Run("احصل على مفتاح ↗"));
            link.Click += (_, _) => OpenInShell(url);
            header.Children.Add(new TextBlock(link) { Margin = new Thickness(16, 3, 0, 0) });
        }

        root.Children.Add(header);
        var description = string.Join("  ", new[] { definition.Description, definition.PricingNote }.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (description.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = BrushOf("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Margin = new Thickness(0, 2, 0, 8),
            });
        }

        // السطر الثاني: خانة المفتاح + الأزرار
        var inputRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var password = new PasswordBox
        {
            FlowDirection = FlowDirection.LeftToRight,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = "الصق المفتاح هنا",
        };
        var plain = new TextBox
        {
            FlowDirection = FlowDirection.LeftToRight,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 2, 6, 2),
            Visibility = Visibility.Collapsed,
        };
        var boxes = new Grid();
        boxes.Children.Add(password);
        boxes.Children.Add(plain);
        Grid.SetColumn(boxes, 0);
        inputRow.Children.Add(boxes);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var pasteButton = new Button { Content = "لصق" };
        var showButton = new Button { Content = "إظهار" };
        var saveButton = new Button { Content = "حفظ واختبار", Style = (Style)FindResource("PrimaryButton") };
        var deleteButton = new Button { Content = "حذف" };
        buttons.Children.Add(pasteButton);
        buttons.Children.Add(showButton);
        buttons.Children.Add(saveButton);
        buttons.Children.Add(deleteButton);
        Grid.SetColumn(buttons, 1);
        inputRow.Children.Add(buttons);
        root.Children.Add(inputRow);

        // السطر الثالث: الحالة
        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(status);
        ShowSavedState(definition.Id, status);

        string CurrentInput() => plain.Visibility == Visibility.Visible ? plain.Text : password.Password;

        pasteButton.Click += (_, _) =>
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(status, "الحافظة فارغة. انسخ المفتاح من موقع الخدمة أولًا.", "WarnBrush");
                return;
            }

            password.Password = text.Trim();
            plain.Text = text.Trim();
            SetStatus(status, "تم اللصق. اضغط \"حفظ واختبار\".", "MutedBrush");
        };

        showButton.Click += (_, _) =>
        {
            if (plain.Visibility == Visibility.Visible)
            {
                password.Password = plain.Text;
                plain.Visibility = Visibility.Collapsed;
                password.Visibility = Visibility.Visible;
                showButton.Content = "إظهار";
            }
            else
            {
                plain.Text = password.Password;
                password.Visibility = Visibility.Collapsed;
                plain.Visibility = Visibility.Visible;
                showButton.Content = "إخفاء";
            }
        };

        saveButton.Click += async (_, _) =>
        {
            var entered = CurrentInput();
            if (!string.IsNullOrWhiteSpace(entered))
            {
                if (SecureKeyStore.Clean(entered) is null)
                {
                    SetStatus(status, "المفتاح فارغ.", "FailBrush");
                    return;
                }

                _ws.SaveKey(definition.Id, entered);
                password.Clear();
                plain.Clear();
            }
            else if (!_ws.HasKey(definition.Id))
            {
                SetStatus(status, "الصق المفتاح أولًا ثم اضغط \"حفظ واختبار\".", "WarnBrush");
                return;
            }

            buttons.IsEnabled = false;
            SetStatus(status, "جارٍ اختبار المفتاح…", "MutedBrush");
            try
            {
                var result = await _ws.TestKeyAsync(definition.Id, Http);
                var brush = result.Status switch
                {
                    ConnectionStatus.Ok => "OkBrush",
                    ConnectionStatus.Warning => "WarnBrush",
                    _ => "FailBrush",
                };
                SetStatus(status, $"{result.Message}   ({Mask(_ws.Keys.Get(definition.Id))})", brush);
                if (result.Details is not null)
                {
                    status.ToolTip = result.Details;
                }
            }
            finally
            {
                buttons.IsEnabled = true;
                BuildModelsPanel();
                UpdateStatusBar();
            }
        };

        deleteButton.Click += (_, _) =>
        {
            if (!_ws.HasKey(definition.Id) || !Confirm($"حذف مفتاح {definition.Name}؟"))
            {
                return;
            }

            _ws.SaveKey(definition.Id, null);
            ShowSavedState(definition.Id, status);
            BuildModelsPanel();
            UpdateStatusBar();
        };

        return new Border { Style = (Style)FindResource("Card"), Child = root };
    }

    private void ShowSavedState(string providerId, TextBlock status)
    {
        if (_ws.HasKey(providerId))
        {
            SetStatus(status, $"✓ مفتاح محفوظ ({Mask(_ws.Keys.Get(providerId))}). اضغط \"حفظ واختبار\" للتأكد أنه يعمل.", "OkBrush");
        }
        else
        {
            SetStatus(status, "لا يوجد مفتاح.", "MutedBrush");
        }
    }

    private void SetStatus(TextBlock target, string text, string brushKey)
    {
        target.Text = text;
        target.Foreground = BrushOf(brushKey);
        target.ToolTip = null;
    }

    private Border CreateBadge(string text, string brushKey) => new()
    {
        Background = BrushOf(brushKey),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(10, 2, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.White, FontSize = 12 },
    };

    private static string Mask(string? key) =>
        key is null ? "-" : key.Length <= 8 ? new string('•', key.Length) : $"{key[..4]}••••{key[^4..]}";

    private void OnAddCustomProvider(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "اختر ملف تعريف الخدمة (JSON)",
            Filter = "ملف تعريف خدمة (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var definition = _ws.AddCustomProvider(dialog.FileName);
            ShowMessage($"تمت إضافة الخدمة \"{definition.Name}\".\nأدخل مفتاحها في هذه الصفحة إن كانت تحتاج مفتاحًا.");
        }
        catch (ProviderDefinitionException ex)
        {
            ShowMessage(ex.Message, MessageBoxImage.Warning);
        }

        BuildKeysPanel();
        BuildModelsPanel();
        UpdateStatusBar();
    }

    private void OnOpenProvidersFolder(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.CustomProviders);
}
