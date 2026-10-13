using System.Net;
using System.Text;

namespace VoiceType.Tests.Support;

/// <summary>
/// يلتقط الطلبات ويعيد ردودًا محددة، لاختبار المزودات بدون إنترنت وبدون استهلاك رصيد.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    public List<CapturedRequest> Requests { get; } = [];

    public static FakeHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public static FakeHttpHandler Sequence(params Func<HttpResponseMessage>[] responses)
    {
        var index = 0;
        return new FakeHttpHandler((_, _) =>
        {
            var factory = responses[Math.Min(index, responses.Length - 1)];
            index++;
            return Task.FromResult(factory());
        });
    }

    public HttpClient CreateClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.ToString(),
            body));
        return await _responder(request, cancellationToken);
    }
}

public sealed record CapturedRequest(
    string Method,
    Uri Uri,
    Dictionary<string, string> Headers,
    string? ContentType,
    byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}
