using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoiceType.App.Services;
using VoiceType.Core.Speech;
using VoiceType.Settings;

namespace VoiceType.App;

/// <summary>
/// النافذة الرئيسية: واجهة HTML عربية داخل WebView2، تتواصل مع البرنامج عبر رسائل JSON
/// (طلب ← رد، وأحداث من البرنامج للواجهة). منطق الطلبات في MainWindow.Bridge.cs.
/// </summary>
public partial class MainWindow : Window
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private bool _ready;
    private bool _forceClose;
    private string? _pendingPage;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeWebAsync();
        Closing += (_, e) =>
        {
            if (!_forceClose && !App.Current.IsExiting)
            {
                // إغلاق النافذة لا يوقف البرنامج: يبقى زر المايك والأيقونة بجوار الساعة
                e.Cancel = true;
                Hide();
            }
        };

        App.Dictation.HistoryAdded += item => Dispatcher.InvokeAsync(() => Push("dictation.history", item));
        App.Dictation.LearningUpdated += () => Dispatcher.InvokeAsync(() => Push("learning.updated", null));
        App.Dictation.TestResult += (text, error) => Dispatcher.InvokeAsync(() => Push("dictation.test", new { text, error }));
        App.Dictation.LiveText += (text, interim) => Dispatcher.InvokeAsync(() =>
        {
            if (App.Dictation.Target == DictationTarget.ReturnOnly)
            {
                Push("dictation.live", new { text, interim });
            }
        });
        App.Dictation.LevelChanged += level =>
        {
            if (App.Dictation.Target == DictationTarget.ReturnOnly)
            {
                Dispatcher.InvokeAsync(() => Push("dictation.level", Math.Round(level, 3)));
            }
        };
    }

    public void Open(string? page)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (page is not null)
        {
            if (_ready)
            {
                Push("app.navigate", page);
            }
            else
            {
                _pendingPage = page;
            }
        }
    }

    public void CloseForExit()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>يرسل حدثًا للواجهة.</summary>
    public void Push(string name, object? data)
    {
        // لازم ننتقل لخيط الواجهة الأول: مجرد قراءة Web.CoreWebView2 من خيط تاني بترمي خطأ
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Push(name, data));
            return;
        }

        if (!_ready || Web.CoreWebView2 is null)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "event", name, data }, Json));
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.Ensure(AppPaths.WebViewData));
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is not WebView2RuntimeNotFoundException and (System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException or IOException))
        {
            LoadingText.Text = "تعذّر تشغيل واجهة البرنامج:\n" + ex.Message + "\nزر المايك يعمل بدونها. أغلق البرنامج وافتحه مرة أخرى.";
            return;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            LoadingText.Text = "يحتاج البرنامج مكوّن Microsoft Edge WebView2 لعرض الواجهة.\nسيُفتح رابط التنزيل الآن، ثبّته ثم افتح البرنامج مرة أخرى.\nزر المايك يعمل بدونه.";
            OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(e.Uri);
        };
        core.NavigationCompleted += (_, _) =>
        {
            _ready = true;
            Web.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;
            if (_pendingPage is not null)
            {
                Push("app.navigate", _pendingPage);
                _pendingPage = null;
            }
        };
        // الواجهة تُقدَّم من الملفات المضمّنة داخل البرنامج على عنوان داخلي (لا اتصال بالإنترنت)
        core.AddWebResourceRequestedFilter(UiResources.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = UiResources.Respond(core.Environment, e.Request.Uri);
        core.Navigate(UiResources.Origin + "/index.html");
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        var id = message?["id"]?.GetValue<int>() ?? 0;
        var method = message?["method"]?.GetValue<string>() ?? string.Empty;
        var parameters = message?["params"] as JsonObject ?? new JsonObject();

        // ملفات مسحوبة إلى الواجهة: نحصل على مساراتها من WebView2
        var droppedFiles = new List<string>();
        if (e.AdditionalObjects is { } objects)
        {
            foreach (var item in objects)
            {
                if (item is CoreWebView2File file)
                {
                    droppedFiles.Add(file.Path);
                }
            }
        }

        try
        {
            var result = await HandleAsync(method, parameters, droppedFiles);
            Reply(id, ok: true, result, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Reply(id, ok: false, null, FriendlyMessage(ex));
        }
    }

    private void Reply(int id, bool ok, object? data, string? error)
    {
        if (Web.CoreWebView2 is null)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "result", id, ok, data, error }, Json));
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AllEnginesFailedException all => all.Failures.Count > 0 ? all.Failures[^1].Message : all.UserMessage,
        SpeechException speech => speech.UserMessage,
        HttpRequestException http when http.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "المفتاح غير صحيح أو لا يملك صلاحية.",
        HttpRequestException => "تعذّر الاتصال بالإنترنت أو بالخدمة. حاول مرة أخرى.",
        TaskCanceledException => "انتهت المهلة أو أُلغيت العملية.",
        _ => ex.Message,
    };

    internal static void OpenUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    internal static void OpenFolder(string folder, string? select = null)
    {
        Directory.CreateDirectory(folder);
        if (select is not null && File.Exists(select))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{select}\"") { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }
}

/// <summary>
/// يقدّم ملفات الواجهة المضمّنة في البرنامج (HTML وCSS وJS والخطوط ومكتبة المجسمات) على عنوان داخلي.
/// كل الملفات داخل البرنامج نفسه، فلا تحتاج الواجهة إنترنت.
/// </summary>
internal static class UiResources
{
    public const string Origin = "https://app.voicetype.local";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
    };

    public static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment environment, string uri)
    {
        var name = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? Path.GetFileName(parsed.AbsolutePath) : string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            name = "index.html";
        }

        using var stream = typeof(UiResources).Assembly.GetManifestResourceStream($"VoiceType.App.ui.{name}");
        if (stream is null)
        {
            return environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
        }

        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        var type = Types.TryGetValue(Path.GetExtension(name), out var t) ? t : "application/octet-stream";
        return environment.CreateWebResourceResponse(copy, 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-cache");
    }
}
