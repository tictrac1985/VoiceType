using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Templating;

namespace VoiceType.Providers;

/// <summary>يجلب قائمة النماذج المتاحة من المزود (للاختيار منها بدل كتابة الاسم).</summary>
public static class ModelLister
{
    public static bool Supports(ProviderDefinition definition) => definition.ModelsList is { Url.Length: > 0 };

    public static async Task<IReadOnlyList<string>> ListAsync(
        ProviderDefinition definition,
        string? apiKey,
        HttpClient http,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        var list = definition.ModelsList ?? throw new InvalidOperationException("هذه الخدمة لا تدعم جلب قائمة النماذج.");
        var context = new TemplateContext();
        foreach (var (name, value) in definition.Variables)
        {
            context.Set(name, value);
        }

        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
        {
            context.Set(name, value);
        }

        var url = TemplateRenderer.Render(list.Url, context);
        var auth = definition.Request.Auth;
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        if (auth.Type == AuthDefinition.QueryParameter && hasKey)
        {
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Uri.EscapeDataString(auth.Name!) + "=" + Uri.EscapeDataString(apiKey!);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hasKey)
        {
            switch (auth.Type)
            {
                case AuthDefinition.Bearer:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    break;
                case AuthDefinition.Header:
                    request.Headers.TryAddWithoutValidation(auth.Name!, string.IsNullOrWhiteSpace(auth.Prefix) ? apiKey : $"{auth.Prefix} {apiKey}");
                    break;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("رد غير مفهوم من الخدمة.", ex);
        }

        if (JsonPathLite.Select(root, list.ItemsPath) is not JsonArray items)
        {
            return [];
        }

        return items
            .Select(item => JsonPathLite.SelectString(item, list.IdPath))
            .OfType<string>()
            .Select(id => list.StripPrefix is { Length: > 0 } prefix && id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id)
            .Where(id => list.Contains is not { Count: > 0 } filters ||
                         filters.Any(f => id.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
