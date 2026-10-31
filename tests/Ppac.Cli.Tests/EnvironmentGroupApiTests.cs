using System.Net;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

public class EnvironmentGroupApiTests
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;
    private const string ListUrl = "https://api.powerplatform.com/environmentmanagement/environmentGroups?api-version=2024-10-01";

    private static string Items(int from, int count) =>
        string.Join(",", Enumerable.Range(from, count).Select(i => $"{{\"id\":\"g{i}\",\"displayName\":\"Group {i}\"}}"));

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    [Fact]
    public void Requests_use_the_documented_urls()
    {
        Assert.Equal(ListUrl, EnvironmentGroupApi.ListRequest(Cloud).Url);
        Assert.Equal(HttpMethod.Get, EnvironmentGroupApi.ListRequest(Cloud).Method);

        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environmentGroups/g1?api-version=2024-10-01",
            EnvironmentGroupApi.GetRequest(Cloud, "g1").Url);
        Assert.Contains("/environmentGroups/a%2Fb%20c?", EnvironmentGroupApi.GetRequest(Cloud, "a/b c").Url);
    }

    [Fact]
    public async Task List_reads_the_value_array_from_the_documented_url()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, $"{{\"value\":[{Items(1, 3)}]}}"));

        var groups = await EnvironmentGroupApi.ListAsync(Api(handler), Cloud, null, CancellationToken.None);

        Assert.Equal(3, groups.Count);
        Assert.Equal(ListUrl, Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task List_follows_nextLink_until_done()
    {
        var handler = new FakeHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("page=2")
                ? (HttpStatusCode.OK, $"{{\"value\":[{Items(3, 2)}]}}")
                : (HttpStatusCode.OK,
                    $"{{\"value\":[{Items(1, 2)}],\"@odata.nextLink\":\"https://api.powerplatform.com/next?page=2\"}}"));

        var groups = await EnvironmentGroupApi.ListAsync(Api(handler), Cloud, null, CancellationToken.None);

        Assert.Equal(4, groups.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task List_stops_at_top_without_fetching_more_pages()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            $"{{\"value\":[{Items(1, 5)}],\"@odata.nextLink\":\"https://api.powerplatform.com/next\"}}"));

        var groups = await EnvironmentGroupApi.ListAsync(Api(handler), Cloud, 3, CancellationToken.None);

        Assert.Equal(3, groups.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_treats_204_no_content_as_an_empty_list()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));

        var groups = await EnvironmentGroupApi.ListAsync(Api(handler), Cloud, null, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task List_does_not_loop_forever_when_nextLink_repeats_the_same_url()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            $"{{\"value\":[{Items(1, 1)}],\"@odata.nextLink\":\"{ListUrl}\"}}"));

        var groups = await EnvironmentGroupApi.ListAsync(Api(handler), Cloud, null, CancellationToken.None);

        Assert.Single(groups);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_returns_the_group_and_204_returns_null()
    {
        var ok = new FakeHandler(_ => (HttpStatusCode.OK, "{\"id\":\"g1\",\"displayName\":\"Prod\"}"));
        var group = await EnvironmentGroupApi.GetAsync(Api(ok), Cloud, "g1", CancellationToken.None);
        Assert.Equal("Prod", group!["displayName"]!.ToString());

        var none = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));
        Assert.Null(await EnvironmentGroupApi.GetAsync(Api(none), Cloud, "g1", CancellationToken.None));
    }

    [Fact]
    public async Task Get_surfaces_404_and_400_as_ApiException()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest })
        {
            var handler = new FakeHandler(_ => (status, "{\"title\":\"nope\"}"));
            var ex = await Assert.ThrowsAsync<ApiException>(
                () => EnvironmentGroupApi.GetAsync(Api(handler), Cloud, "g1", CancellationToken.None));
            Assert.Equal(status, ex.StatusCode);
        }
    }
}
