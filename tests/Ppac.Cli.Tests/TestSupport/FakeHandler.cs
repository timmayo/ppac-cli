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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var (status, body) = _respond(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}