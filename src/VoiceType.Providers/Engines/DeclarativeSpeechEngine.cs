using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Templating;

namespace VoiceType.Providers.Engines;

/// <summary>
/// محرك تفريغ يعمل بالكامل من ملف تعريف (Custom Provider).
/// كل المزودات المدمجة تستخدم هذا المحرك نفسه.
/// </summary>
public sealed class DeclarativeSpeechEngine : ISpeechEngine
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ProviderDefinition _definition;
    private readonly ProviderInstance _instance;
    private readonly HttpClient _http;

    public DeclarativeSpeechEngine(ProviderDefinition definition, ProviderInstance instance, HttpClient http)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _http = http ?? throw new ArgumentNullException(nameof(http));

        Model = instance.Model ?? definition.DefaultModel ?? definition.Models.FirstOrDefault()?.Id;
        Id = string.IsNullOrWhiteSpace(instance.Id)
            ? (Model is null ? definition.Id : $"{definition.Id}:{Model}")
            : instance.Id;
        DisplayName = instance.Label ?? (Model is null ? definition.Name : $"{definition.Name} · {Model}");
        Capabilities = new SpeechCapabilities
        {
            Formats = definition.Capabilities.Formats?.Select(AudioFormats.Normalize).Distinct().ToList(),
            MaxFileMegabytes = definition.Capabilities.MaxFileMb,
            LanguageRequired = definition.Capabilities.LanguageRequired,
            SupportsPrompt = definition.Capabilities.SupportsPrompt,
            RequiresNetwork = true,
        };
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string? Model { get; }

    public SpeechCapabilities Capabilities { get; }

    public ProviderDefinition Definition => _definition;

    /// <summary>حد الطلبات في الدقيقة لكل مفتاح على حدة (كل حساب له حده).</summary>
    private string GateKey => Resilience.KeyHealth.Fingerprint(_definition.Id, _instance.ApiKey);

    /// <summary>كم سينتظر هذا المفتاح قبل الإرسال بسبب حد الطلبات في الدقيقة.</summary>
    public TimeSpan EstimatedGateWait =>
        _definition.RequestsPerMinute is > 0 and var rpm ? RateGate.For(GateKey, rpm).EstimatedWait : TimeSpan.Zero;

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        EnsureCanSend(audio, options);

        var context = BuildContext(audio, options);
        using var request = BuildRequest(audio, context);

        // احترام حد الطلبات في الدقيقة (مثل الخطط المجانية) قبل الإرسال، ثم تبدأ مهلة الرد
        if (_definition.RequestsPerMinute is > 0 and var rpm)
        {
            await RateGate.For(GateKey, rpm).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GetTimeout());

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechException(SpeechErrorKind.Timeout, Id, $"No response within {GetTimeout().TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechException(SpeechErrorKind.Network, Id, ex.Message, innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw ErrorMapper.FromResponse(Id, response, body, _definition.Response.ErrorPath);
            }

            if (_definition.Response.Polling is { Url.Length: > 0 } polling)
            {
                try
                {
                    body = await PollUntilDoneAsync(body, context, polling, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new SpeechException(SpeechErrorKind.Timeout, Id, $"Result not ready within {GetTimeout().TotalSeconds:0}s.");
                }
            }

            stopwatch.Stop();

            var (text, cost) = ParseResponse(body, response.Content.Headers.ContentType?.MediaType);
            return new TranscriptionResult
            {
                Text = text.Trim(),
                EngineId = Id,
                Model = Model,
                Elapsed = stopwatch.Elapsed,
                Cost = cost,
            };
        }
    }

    /// <summary>
    /// وصف الطلب بدون إرساله، مع إخفاء المفتاح. يُستخدم في زر Test وأداة المقارنة (--dry-run).
    /// </summary>
    public RequestPreview Preview(AudioPayload audio, TranscriptionOptions options)
    {
        var context = BuildContext(audio, options, apiKeyOverride: "***");
        using var request = BuildRequest(audio, context, includeRealKey: false);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

        string bodyDescription = _definition.Request.Body.Type switch
        {
            BodyDefinition.Json => (TemplateRenderer.RenderJson(_definition.Request.Body.Template,
                    BuildContext(audio, options, apiKeyOverride: "***", audioBase64Override: $"<{audio.Data.Length} bytes base64>"))
                ?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}"),
            BodyDefinition.Multipart => "multipart/form-data: " + string.Join(", ",
                new[] { $"{_definition.Request.Body.FileField}=<{audio.FileName}, {audio.Data.Length} bytes>" }
                    .Concat(_definition.Request.Body.Fields
                        .Select(f => (f.Key, Value: TemplateRenderer.Render(f.Value, context)))
                        .Where(f => f.Value.Length > 0)
                        .Select(f => $"{f.Key}={f.Value}"))),
            _ => $"<{audio.Data.Length} bytes, {audio.MimeType}>",
        };

        return new RequestPreview(request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, headers, bodyDescription);
    }

    private void EnsureCanSend(AudioPayload audio, TranscriptionOptions options)
    {
        if (!Capabilities.AcceptsFormat(audio.Format))
        {
            throw new SpeechException(
                SpeechErrorKind.UnsupportedFormat,
                Id,
                $"Format '{audio.Format}' not in [{string.Join(", ", Capabilities.Formats ?? [])}].");
        }

        if (Capabilities.MaxFileMegabytes is { } max && audio.SizeInMegabytes > max)
        {
            throw new SpeechException(
                SpeechErrorKind.PayloadTooLarge,
                Id,
                $"Audio is {audio.SizeInMegabytes:0.0} MB, limit is {max} MB.");
        }

        if (Capabilities.LanguageRequired && string.IsNullOrWhiteSpace(options.Language))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, "This provider requires a language (e.g. 'ar').");
        }

        if (string.IsNullOrWhiteSpace(Model) && TemplateUsesVariable(TemplateContext.Model))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, "No model selected.");
        }

        var auth = _definition.Request.Auth;
        if (auth.Type != AuthDefinition.None && !auth.Optional && string.IsNullOrWhiteSpace(_instance.ApiKey))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, "API key is missing.");
        }
    }

    private bool TemplateUsesVariable(string name)
    {
        var request = _definition.Request;
        var all = new List<string> { request.Url };
        all.AddRange(request.Query.Values);
        all.AddRange(request.Headers.Values);
        all.AddRange(request.Body.Fields.Values);
        if (request.Body.Template is not null)
        {
            all.Add(request.Body.Template.ToJsonString());
        }

        return all.SelectMany(TemplateRenderer.GetVariableNames).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private TemplateContext BuildContext(
        AudioPayload audio,
        TranscriptionOptions options,
        string? apiKeyOverride = null,
        string? audioBase64Override = null)
    {
        var context = new TemplateContext();
        foreach (var (name, value) in _definition.Variables)
        {
            context.Set(name, value);
        }

        foreach (var (name, value) in _instance.Variables)
        {
            context.Set(name, value);
        }

        context
            .Set(TemplateContext.Model, Model)
            .Set(TemplateContext.Language, options.Language?.Trim())
            .Set(TemplateContext.Prompt, Capabilities.SupportsPrompt ? options.Prompt : null)
            .Set(TemplateContext.Temperature, options.Temperature?.ToString(CultureInfo.InvariantCulture))
            .Set(TemplateContext.AudioFormat, audio.Format)
            .Set(TemplateContext.AudioMime, audio.MimeType)
            .Set(TemplateContext.FileName, audio.FileName)
            .Set(TemplateContext.ApiKey, apiKeyOverride ?? _instance.ApiKey);

        if (audioBase64Override is not null)
        {
            context.Set(TemplateContext.AudioBase64, audioBase64Override);
        }
        else
        {
            context.SetLazy(TemplateContext.AudioBase64, () => Convert.ToBase64String(audio.Data));
        }

        return context;
    }

    /// <summary>إذا ردت الخدمة بأن التفريغ ما زال جاريًا، نسأل عن النتيجة كل ثانية حتى تكتمل.</summary>
    private async Task<string> PollUntilDoneAsync(string body, TemplateContext context, PollingDefinition polling, CancellationToken cancellationToken)
    {
        var delay = 200;
        while (true)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return body;
            }

            var status = JsonPathLite.SelectString(root, polling.StatusPath);
            if (status is null || !polling.Pending.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                return body;
            }

            var id = JsonPathLite.SelectString(root, polling.IdPath)
                     ?? throw new SpeechException(SpeechErrorKind.InvalidResponse, Id, $"Pending result without '{polling.IdPath}'.");
            context.Set("id", id);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, Math.Max(250, polling.IntervalMs));

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(TemplateRenderer.Render(polling.Url, context), new Dictionary<string, string>(), context, AuthQuery()));
            AddAuthHeaders(request, includeRealKey: true);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SpeechException(SpeechErrorKind.Network, Id, ex.Message, innerException: ex);
            }

            using (response)
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw ErrorMapper.FromResponse(Id, response, body, _definition.Response.ErrorPath);
                }
            }
        }
    }

    private (string Name, string Value)? AuthQuery()
    {
        var auth = _definition.Request.Auth;
        var hasKey = !string.IsNullOrWhiteSpace(_instance.ApiKey);
        var sendAuth = auth.Type != AuthDefinition.None && (hasKey || !auth.Optional);
        return sendAuth && auth.Type == AuthDefinition.QueryParameter ? (auth.Name!, _instance.ApiKey ?? string.Empty) : null;
    }

    private void AddAuthHeaders(HttpRequestMessage request, bool includeRealKey)
    {
        var auth = _definition.Request.Auth;
        var key = includeRealKey ? _instance.ApiKey : "***";
        var hasKey = !string.IsNullOrWhiteSpace(_instance.ApiKey);
        if (auth.Type == AuthDefinition.None || (!hasKey && auth.Optional))
        {
            return;
        }

        switch (auth.Type)
        {
            case AuthDefinition.Bearer:
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                break;
            case AuthDefinition.Header:
                request.Headers.TryAddWithoutValidation(auth.Name!, string.IsNullOrWhiteSpace(auth.Prefix) ? key : $"{auth.Prefix} {key}");
                break;
        }
    }

    private HttpRequestMessage BuildRequest(AudioPayload audio, TemplateContext context, bool includeRealKey = true)
    {
        var def = _definition.Request;
        var auth = def.Auth;
        var key = includeRealKey ? _instance.ApiKey : "***";
        var hasKey = !string.IsNullOrWhiteSpace(_instance.ApiKey);
        var sendAuth = auth.Type != AuthDefinition.None && (hasKey || !auth.Optional);
        var authValue = string.IsNullOrWhiteSpace(auth.Prefix) ? key : $"{auth.Prefix} {key}";

        var url = BuildUrl(TemplateRenderer.Render(def.Url, context), def.Query, context,
            sendAuth && auth.Type == AuthDefinition.QueryParameter ? (auth.Name!, key ?? string.Empty) : null);

        var request = new HttpRequestMessage(new HttpMethod(def.Method.ToUpperInvariant()), url);

        if (sendAuth)
        {
            switch (auth.Type)
            {
                case AuthDefinition.Bearer:
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                    break;
                case AuthDefinition.Header:
                    request.Headers.TryAddWithoutValidation(auth.Name!, authValue);
                    break;
            }
        }

        foreach (var (name, template) in def.Headers)
        {
            var value = TemplateRenderer.Render(template, context);
            if (value.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Content = def.Body.Type switch
        {
            BodyDefinition.Json => BuildJsonContent(context),
            BodyDefinition.Raw => BuildRawContent(audio, context),
            _ => BuildMultipartContent(audio, context),
        };

        return request;
    }

    private static Uri BuildUrl(
        string baseUrl,
        Dictionary<string, string> query,
        TemplateContext context,
        (string Name, string Value)? authParameter)
    {
        var parameters = query
            .Select(q => (q.Key, Value: TemplateRenderer.Render(q.Value, context)))
            .Where(q => q.Value.Length > 0)
            .ToList();

        if (authParameter is { } auth)
        {
            parameters.Add((auth.Name, auth.Value));
        }

        if (parameters.Count == 0)
        {
            return new Uri(baseUrl);
        }

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var queryString = string.Join("&", parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(baseUrl + separator + queryString);
    }

    private HttpContent BuildMultipartContent(AudioPayload audio, TemplateContext context)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, template) in _definition.Request.Body.Fields)
        {
            var value = TemplateRenderer.Render(template, context);
            if (value.Length > 0)
            {
                content.Add(new StringContent(value, Encoding.UTF8), name);
            }
        }

        var file = new ByteArrayContent(audio.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(audio.MimeType);
        content.Add(file, _definition.Request.Body.FileField, audio.FileName);
        return content;
    }

    private HttpContent BuildJsonContent(TemplateContext context)
    {
        var json = TemplateRenderer.RenderJson(_definition.Request.Body.Template, context) ?? new JsonObject();
        return new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private HttpContent BuildRawContent(AudioPayload audio, TemplateContext context)
    {
        var content = new ByteArrayContent(audio.Data);
        var contentType = TemplateRenderer.Render(_definition.Request.Body.ContentType ?? "{audio_mime}", context);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(contentType) ? audio.MimeType : contentType);
        return content;
    }

    private (string Text, double? Cost) ParseResponse(string body, string? mediaType)
    {
        var textPath = _definition.Response.TextPath.Trim();
        var looksLikeJson = body.TrimStart() is { Length: > 0 } trimmed && (trimmed[0] == '{' || trimmed[0] == '[');

        if (!looksLikeJson)
        {
            if (textPath == "$" || mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
            {
                return (body, null);
            }

            throw new SpeechException(SpeechErrorKind.InvalidResponse, Id, "Response is not JSON.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new SpeechException(SpeechErrorKind.InvalidResponse, Id, "Response JSON is malformed.", innerException: ex);
        }

        var text = JsonPathLite.SelectString(root, textPath)
            ?? throw new SpeechException(SpeechErrorKind.InvalidResponse, Id, $"Path '{textPath}' not found in response.");

        if (_definition.Response.CleanupChatText)
        {
            text = ChatTextCleanup.Clean(text);
        }

        var cost = _definition.Response.CostPath is { Length: > 0 } costPath
            ? JsonPathLite.SelectNumber(root, costPath)
            : null;

        return (text, cost);
    }

    private TimeSpan GetTimeout()
    {
        var seconds = _instance.TimeoutSeconds ?? _definition.TimeoutSeconds;
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultTimeout;
    }
}

public sealed record RequestPreview(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{Method} {Url}");
        foreach (var (name, value) in Headers)
        {
            builder.AppendLine($"{name}: {value}");
        }

        builder.AppendLine();
        builder.Append(Body);
        return builder.ToString();
    }
}

/// <summary>يضمن ألا تتجاوز الطلبات لمزود معين حدًا في الدقيقة، بتوزيعها على فترات متساوية.</summary>
/// <summary>
/// حد الطلبات في الدقيقة بنافذة منزلقة: يسمح بعدد الطلبات المسموح به خلال أي 60 ثانية،
/// فلا ينتظر المستخدم بين جملتين متتاليتين إلا إذا وصل فعلًا للحد.
/// </summary>
internal sealed class RateGate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RateGate> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly int _limit;

    private RateGate(int requestsPerMinute) => _limit = Math.Max(1, requestsPerMinute);

    public static RateGate For(string key, int requestsPerMinute) =>
        Gates.GetOrAdd($"{key}:{requestsPerMinute}", _ => new RateGate(requestsPerMinute));

    public TimeSpan EstimatedWait
    {
        get
        {
            lock (_sent)
            {
                Trim(DateTimeOffset.UtcNow);
                if (_sent.Count < _limit)
                {
                    return TimeSpan.Zero;
                }

                var wait = _sent.Peek() + Window - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = EstimatedWait;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            lock (_sent)
            {
                Trim(DateTimeOffset.UtcNow);
                _sent.Enqueue(DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= Window)
        {
            _sent.Dequeue();
        }
    }
}
