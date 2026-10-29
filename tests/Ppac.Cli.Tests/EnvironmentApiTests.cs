using System.Net;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

public class EnvironmentApiTests
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private static string Items(int from, int count) =>
        string.Join(",", Enumerable.Range(from, count).Select(i => $"{{\"id\":\"e{i}\"}}"));

    [Fact]
    public async Task List_builds_the_documented_url()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"value\":[]}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        await EnvironmentApi.ListAsync(api, Cloud, null, null, CancellationToken.None);

        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments?api-version=2024-10-01",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task List_encodes_filter_and_adds_top()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"value\":[]}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        await EnvironmentApi.ListAsync(api, Cloud, "type eq 'Sandbox'", 5, CancellationToken.None);

        var query = Assert.Single(handler.Requests).RequestUri!.Query;
        Assert.Contains("api-version=2024-10-01", query);
        Assert.Contains("%24filter=type%20eq%20%27Sandbox%27", query.Replace("$filter", "%24filter"));
        Assert.Contains("top=5", query);
    }

    [Fact]
    public async Task List_follows_nextlink_until_done()
    {
        var handler = new FakeHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("page=2")
                ? (HttpStatusCode.OK, $"{{\"value\":[{Items(3, 2)}]}}")
                : (HttpStatusCode.OK,
                    $"{{\"value\":[{Items(1, 2)}],\"@odata.nextlink\":\"https://api.powerplatform.com/next?page=2\"}}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var result = await EnvironmentApi.ListAsync(api, Cloud, null, null, CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task List_accepts_alternate_nextlink_casing()
    {
        var handler = new FakeHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("page=2")
                ? (HttpStatusCode.OK, $"{{\"value\":[{Items(2, 1)}]}}")
                : (HttpStatusCode.OK,
                    $"{{\"value\":[{Items(1, 1)}],\"@odata.nextLink\":\"https://api.powerplatform.com/next?page=2\"}}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var result = await EnvironmentApi.ListAsync(api, Cloud, null, null, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task List_stops_at_top_and_trims_extra_items()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            $"{{\"value\":[{Items(1, 5)}],\"@odata.nextlink\":\"https://api.powerplatform.com/next\"}}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var result = await EnvironmentApi.ListAsync(api, Cloud, null, 3, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_builds_the_documented_url()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"id\":\"abc\"}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var result = await EnvironmentApi.GetAsync(api, Cloud, "abc", null, CancellationToken.None);

        Assert.Equal("abc", result["id"]!.ToString());
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/abc?api-version=2024-10-01",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Get_passes_select_and_escapes_the_id()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        await EnvironmentApi.GetAsync(api, Cloud, "a/b c", "displayName,state", CancellationToken.None);

        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Contains("/environments/a%2Fb%20c", uri.AbsoluteUri);
        Assert.Contains("select=displayName%2Cstate", uri.Query);
    }

    [Fact]
    public async Task Get_surfaces_404_as_ApiException()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NotFound, ""));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => EnvironmentApi.GetAsync(api, Cloud, "missing", null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}