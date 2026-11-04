using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoiceType.Providers.Engines;

namespace VoiceType.Providers.Rewrite;

/// <summary>نموذج لغوي بيكتب نص: للملخص، و«صيغها لي»، والترجمة.</summary>
public interface ITextEngine
{
    /// <summary>الاسم للعرض والسجل (مثل «Ollama · qwen3»).</summary>
    string Name { get; }

    Task<string> GenerateAsync(string instructions, string text, double temperature, CancellationToken cancellationToken);
}

/// <summary>أنواع الخوادم اللي صدى يقدر يكلمها غير Gemini.</summary>
public enum TextServerKind
{
    /// <summary>Ollama (على الجهاز، أو موديلاته السحابية من خلاله).</summary>
    Ollama,

    /// <summary>Ollama Cloud مباشرة (ollama.com) بمفتاح.</summary>
    OllamaCloud,

    /// <summary>LM Studio أو أي خادم بنفس طريقة OpenAI (‎/v1/chat/completions).</summary>
    OpenAiCompatible,
}

/// <summary>
/// كلام مع Ollama أو LM Studio (على الجهاز، أو على جهاز تاني، أو على السحابة).
/// - Ollama: ‎POST /api/chat (والموديلات السحابية زي ‎gpt-oss:120b-cloud بتشتغل من خلاله بعد ‎ollama signin).
/// - Ollama Cloud: نفس الطريقة على https://ollama.com بمفتاح (Authorization: Bearer).
/// - LM Studio: ‎POST /v1/chat/completions (والعنوان ممكن يكون جهاز تاني أو LM Link).
/// الموديلات اللي «بتفكر» (‎&lt;think&gt;) بنشيل تفكيرها ونرجّع الإجابة بس.
/// </summary>
public sealed partial class TextServerEngine : ITextEngine
{
    public const string OllamaLocalUrl = "http://127.0.0.1:11434";

    public const string OllamaCloudUrl = "https://ollama.com";

    public const string LmStudioLocalUrl = "http://127.0.0.1:1234";

    private readonly HttpClient _http;
    private readonly TextServerKind _kind;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    public TextServerEngine(HttpClient http, TextServerKind kind, string baseUrl, string model, string? apiKey = null, string? name = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _kind = kind;
        _baseUrl = NormalizeUrl(baseUrl);
        _model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("اختار موديل الأول.", nameof(model)) : model.Trim();
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        Name = name ?? $"{KindName(kind)} · {_model}";
    }

    public string Name { get; }

    /// <summary>الموديلات اللي على الجهاز ممكن تاخد وقت (موديل 27B على كرت مدمج)، فالمهلة طويلة.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public static string KindName(TextServerKind kind) => kind switch
    {
        TextServerKind.Ollama => "Ollama",
        TextServerKind.OllamaCloud => "Ollama Cloud",
        _ => "LM Studio",
    };

    public static string NormalizeUrl(string url)
    {
        var value = (url ?? string.Empty).Trim().TrimEnd('/');
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^3];
        }

        if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("العنوان لازم يبدأ بـ http:// أو https:// (مثال: http://127.0.0.1:11434).");
        }

        return value;
    }

    public async Task<string> GenerateAsync(string instructions, string text, double temperature, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var useThink = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl())
            {
                Content = new StringContent(RequestBody(_kind, _model, instructions, text, temperature, useThink), Encoding.UTF8, "application/json"),
            };
            Authorize(request);
            string json;
            HttpStatusCode status;
            try
            {
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                status = response.StatusCode;
                json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"{Name}: مردّش في الوقت المحدد.");
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"{Name}: مش شغال أو مش متاح على {_baseUrl} ({ex.Message}).", ex);
            }

            if (status == HttpStatusCode.BadRequest && useThink && json.Contains("think", StringComparison.OrdinalIgnoreCase))
            {
                useThink = false; // الموديل ده مش بيدعم إيقاف التفكير: نبعت من غيره
                continue;
            }

            if ((int)status is < 200 or >= 300)
            {
                throw new InvalidOperationException($"{Name}: HTTP {(int)status} {Short(ErrorMessage(json))}".Trim());
            }

            var answer = ExtractText(_kind, json);
            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new InvalidOperationException($"{Name}: رجّع رد فاضي.");
            }

            return ChatTextCleanup.Clean(answer).Trim();
        }

        throw new InvalidOperationException($"{Name}: رفض الطلب.");
    }

    /// <summary>الموديلات المتاحة على الخادم (ونستبعد موديلات الـ embedding).</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, TextServerKind kind, string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        var url = NormalizeUrl(baseUrl) + (kind == TextServerKind.OpenAiCompatible ? "/v1/models" : "/api/tags");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {Short(ErrorMessage(json))}".Trim());
        }

        return ParseModels(kind, json);
    }

    public static IReadOnlyList<string> ParseModels(TextServerKind kind, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("الخادم رد بحاجة مش مفهومة (مش Ollama أو LM Studio؟).", ex);
        }

        var list = (kind == TextServerKind.OpenAiCompatible ? root?["data"] : root?["models"]) as JsonArray;
        var items = list?.Select(m => m is JsonObject o
            ? (kind == TextServerKind.OpenAiCompatible ? (string?)o["id"] : (string?)o["name"] ?? (string?)o["model"])
            : null);
        return (items ?? [])
            .OfType<string>()
            .Where(id => id.Length > 0 && !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string RequestBody(TextServerKind kind, string model, string instructions, string text, double temperature, bool disableThinking = true)
    {
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = instructions },
            new JsonObject { ["role"] = "user", ["content"] = text });
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
        if (kind == TextServerKind.OpenAiCompatible)
        {
            body["temperature"] = temperature;
        }
        else
        {
            body["options"] = new JsonObject { ["temperature"] = temperature };
            if (disableThinking)
            {
                body["think"] = false; // أسرع: الملخص والصياغة مش محتاجين «تفكير»
            }
        }

        return body.ToJsonString();
    }

    public static string ExtractText(TextServerKind kind, string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var content = kind == TextServerKind.OpenAiCompatible
                ? (string?)root?["choices"]?[0]?["message"]?["content"]
                : (string?)root?["message"]?["content"];
            return StripThinking(content ?? string.Empty);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentOutOfRangeException or InvalidOperationException or FormatException)
        {
            return string.Empty; // رد مش متوقع (مصفوفة فاضية، صفحة HTML…): يتعامل كرد فاضي فنكمّل بالبديل
        }
    }

    /// <summary>بيشيل «تفكير» الموديل (‎&lt;think&gt;…&lt;/think&gt;) ويسيب الإجابة.</summary>
    public static string StripThinking(string text)
    {
        var cleaned = ThinkBlock().Replace(text, string.Empty);
        var close = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0)
        {
            cleaned = cleaned[(close + "</think>".Length)..]; // التفكير بدأ قبل أول الرد
        }

        return cleaned.Trim();
    }

    private string ChatUrl() => _baseUrl + (_kind == TextServerKind.OpenAiCompatible ? "/v1/chat/completions" : "/api/chat");

    private void Authorize(HttpRequestMessage request)
    {
        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    private static string ErrorMessage(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            return (string?)root?["error"]?["message"] ?? (string?)root?["error"] ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return json;
        }
    }

    private static string Short(string value) => value.Length > 160 ? value[..160] + "…" : value;

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}

/// <summary>
/// تشغيل Ollama أو LM Studio لو متسطّبين ومش شغالين (مخفي، من غير نوافذ).
/// LM Studio: ‎lms server start. Ollama: ‎ollama serve.
/// </summary>
public static class LocalTextServers
{
    public static string? LmStudioCli
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", OperatingSystem.IsWindows() ? "lms.exe" : "lms");
            return File.Exists(path) ? path : null;
        }
    }

    public static string? OllamaCli
    {
        get
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            if (OperatingSystem.IsWindows() && File.Exists(local))
            {
                return local;
            }

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "ollama.exe" : "ollama");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// عنوان LM Studio الحقيقي على الجهاز ده: LM Studio ممكن يكون متظبط على بورت غير 1234
    /// (زي 1235)، فبنسأل أداته نفسها (‎lms server status --json ← {"running":true,"port":1235}).
    /// null لو LM Studio مش متسطّب أو مش شغال.
    /// </summary>
    public static async Task<string?> LmStudioRunningUrlAsync(CancellationToken cancellationToken)
    {
        var output = await RunCliAsync(LmStudioCli, ["server", "status", "--json"], cancellationToken).ConfigureAwait(false);
        return ParseLmStudioStatus(output);
    }

    public static string? ParseLmStudioStatus(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(output[start..(end + 1)]) as JsonObject;
            return root?["running"] is JsonValue running && running.TryGetValue<bool>(out var isRunning) && isRunning &&
                   root["port"] is JsonValue port && port.TryGetValue<int>(out var number) && number is > 0 and < 65536
                ? $"http://127.0.0.1:{number}"
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> RunCliAsync(string? cli, string[] args, CancellationToken cancellationToken)
    {
        if (cli is null)
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>يحاول يشغّل الخادم ويستنى لحد ما يرد (لحد 20 ثانية). يرجّع false لو مش متسطّب.</summary>
    public static async Task<bool> TryStartAsync(HttpClient http, TextServerKind kind, string baseUrl, CancellationToken cancellationToken)
    {
        if (kind == TextServerKind.OllamaCloud || !IsThisComputer(baseUrl))
        {
            return false;
        }

        var cli = kind == TextServerKind.Ollama ? OllamaCli : LmStudioCli;
        if (cli is null)
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (kind == TextServerKind.Ollama)
            {
                start.ArgumentList.Add("serve");
            }
            else
            {
                start.ArgumentList.Add("server");
                start.ArgumentList.Add("start");
            }

            using var process = Process.Start(start);
            if (process is not null)
            {
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
                _ = process.StandardError.ReadToEndAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            try
            {
                await TextServerEngine.ListModelsAsync(http, kind, baseUrl, null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return false;
    }

    private static bool IsThisComputer(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
