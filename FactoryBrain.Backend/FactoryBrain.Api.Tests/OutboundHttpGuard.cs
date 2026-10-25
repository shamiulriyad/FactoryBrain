using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Primary handler for every <see cref="IHttpClientFactory"/> client in a test
/// host. Requests that match a registered fake are answered locally; anything
/// else is recorded and fails with an exception. A test host therefore cannot
/// reach a real LLM or embedding provider, paid or otherwise.
/// </summary>
public sealed class OutboundHttpGuard : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private readonly ConcurrentQueue<Captured> _handled = new();
    private readonly ConcurrentQueue<Captured> _blocked = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string, HttpResponseMessage> Respond)> _fakes = new();

    /// <summary>Requests answered by a registered fake.</summary>
    public IReadOnlyCollection<Captured> Handled => _handled.ToArray();

    /// <summary>Requests with no fake: these would have left the machine.</summary>
    public IReadOnlyCollection<Captured> Blocked => _blocked.ToArray();

    public OutboundHttpGuard Fake(string host, string path, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _fakes.Add((r => string.Equals(r.RequestUri?.Host, host, StringComparison.OrdinalIgnoreCase)
                         && r.RequestUri!.AbsolutePath.StartsWith(path, StringComparison.Ordinal), respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var captured = new Captured(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        foreach (var (match, respond) in _fakes)
        {
            if (!match(request)) continue;
            _handled.Enqueue(captured);
            return respond(request, body);
        }
        _blocked.Enqueue(captured);
        throw new HttpRequestException($"Outbound HTTP blocked by the test guard: {request.Method} {request.RequestUri?.Host}");
    }

    // The client factory disposes handler chains when they expire; this
    // instance is shared for the host's lifetime, so ignore that.
    protected override void Dispose(bool disposing) { }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Route every named and unnamed client in the container through <paramref name="guard"/>.</summary>
    public static void Install(IServiceCollection services, OutboundHttpGuard guard) =>
        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = guard));
}
