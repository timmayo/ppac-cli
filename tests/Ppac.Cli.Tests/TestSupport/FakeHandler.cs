using System.Net;
using System.Text;

namespace Ppac.Cli.Tests.TestSupport;

/// <summary>An HttpMessageHandler that returns canned responses and records the requests.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _respond;

    public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) =>
        _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The request body of each request (null when there was none), in the same order as Requests.</summary>
    public List<string?> Bodies { get; } = new();

    /// <summary>The Content-Type media type of each request (null when there was no body).</summary>
    public List<string?> ContentTypes { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);

        var (status, body) = _respond(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
