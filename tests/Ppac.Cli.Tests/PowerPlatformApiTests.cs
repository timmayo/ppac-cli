using System.Net;
using Ppac.Cli.Api;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

public class PowerPlatformApiTests
{
    [Fact]
    public async Task Sends_bearer_token_and_json_accept_header()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "token-123");

        await api.GetJsonAsync("https://example.test/x", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("token-123", request.Headers.Authorization?.Parameter);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
    }

    [Fact]
    public async Task Uses_error_message_from_error_body()
    {
        var handler = new FakeHandler(_ =>
            (HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"Not allowed.\"}}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.GetJsonAsync("https://example.test/x", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("Not allowed.", ex.Message);
    }

    [Fact]
    public async Task Reports_empty_error_body()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NotFound, ""));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.GetJsonAsync("https://example.test/x", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("No details were returned.", ex.Message);
    }

    [Fact]
    public async Task Truncates_long_non_json_error_body()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.BadGateway, new string('x', 1000)));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.GetJsonAsync("https://example.test/x", CancellationToken.None));

        Assert.True(ex.Message.Length < 400);
    }
}