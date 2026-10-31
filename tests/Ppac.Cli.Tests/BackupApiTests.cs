using System.Net;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

public class BackupApiTests
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;
    private const string BackupsUrl =
        "https://api.powerplatform.com/environmentmanagement/environments/env1/backups";

    private const string TwoBackups =
        "{\"collection\":[{\"id\":\"b1\",\"label\":\"one\"},{\"id\":\"B2\",\"label\":\"two\"}],\"continuationToken\":null}";

    [Fact]
    public void Requests_use_the_documented_methods_and_urls()
    {
        var list = BackupApi.ListRequest(Cloud, "env1");
        Assert.Equal(HttpMethod.Get, list.Method);
        Assert.Equal($"{BackupsUrl}?api-version=2024-10-01", list.Url);
        Assert.Null(list.Body);

        var create = BackupApi.CreateRequest(Cloud, "env1", "before upgrade");
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal($"{BackupsUrl}?api-version=2024-10-01", create.Url);
        Assert.Equal("{\"label\":\"before upgrade\"}", create.Body!.ToJsonString());

        var delete = BackupApi.DeleteRequest(Cloud, "env1", "b1");
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal($"{BackupsUrl}/b1?api-version=2024-10-01", delete.Url);
        Assert.Null(delete.Body);
    }

    [Fact]
    public void Ids_are_escaped_in_the_url()
    {
        Assert.Contains("/environments/a%2Fb%20c/backups", BackupApi.ListRequest(Cloud, "a/b c").Url);
        Assert.Contains("/backups/x%2Fy?", BackupApi.DeleteRequest(Cloud, "env1", "x/y").Url);
    }

    [Fact]
    public async Task List_reads_the_collection()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, TwoBackups));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var page = await BackupApi.ListAsync(api, Cloud, "env1", CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.ContinuationToken);
        Assert.Equal($"{BackupsUrl}?api-version=2024-10-01", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task List_reports_a_continuation_token_and_tolerates_a_missing_collection()
    {
        var withToken = new FakeHandler(_ => (HttpStatusCode.OK, "{\"collection\":[],\"continuationToken\":\"next\"}"));
        var page = await BackupApi.ListAsync(
            new PowerPlatformApi(new HttpClient(withToken), "t"), Cloud, "env1", CancellationToken.None);
        Assert.Equal("next", page.ContinuationToken);

        var empty = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));
        page = await BackupApi.ListAsync(
            new PowerPlatformApi(new HttpClient(empty), "t"), Cloud, "env1", CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task Create_sends_a_json_body_and_returns_the_backup()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Created, "{\"id\":\"b9\",\"label\":\"x\"}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var backup = await api.SendAsync(BackupApi.CreateRequest(Cloud, "env1", "x"), CancellationToken.None);

        Assert.Equal("b9", backup!["id"]!.ToString());
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
        Assert.Equal("{\"label\":\"x\"}", Assert.Single(handler.Bodies));
        Assert.Equal("application/json", Assert.Single(handler.ContentTypes));
    }

    [Fact]
    public async Task Delete_sends_no_body_and_accepts_204()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var result = await api.SendAsync(BackupApi.DeleteRequest(Cloud, "env1", "b1"), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);
        Assert.Null(Assert.Single(handler.Bodies));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Failed_writes_surface_as_ApiException(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => (status, "{\"error\":{\"message\":\"nope\"}}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.SendAsync(BackupApi.DeleteRequest(Cloud, "env1", "b1"), CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("nope", ex.Message);
    }

    [Fact]
    public async Task Find_matches_the_id_ignoring_case()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, TwoBackups));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var lookup = await BackupApi.FindAsync(api, Cloud, "env1", "b2", CancellationToken.None);

        Assert.True(lookup.Found);
        Assert.Equal("two", lookup.Backup!["label"]!.ToString());
    }

    [Fact]
    public async Task Find_is_conclusive_only_when_there_is_no_continuation_token()
    {
        var complete = new FakeHandler(_ => (HttpStatusCode.OK, TwoBackups));
        var lookup = await BackupApi.FindAsync(
            new PowerPlatformApi(new HttpClient(complete), "t"), Cloud, "env1", "missing", CancellationToken.None);
        Assert.True(lookup.NotFound);

        var partial = new FakeHandler(_ => (HttpStatusCode.OK, "{\"collection\":[],\"continuationToken\":\"next\"}"));
        lookup = await BackupApi.FindAsync(
            new PowerPlatformApi(new HttpClient(partial), "t"), Cloud, "env1", "missing", CancellationToken.None);
        Assert.False(lookup.Found);
        Assert.False(lookup.NotFound);
    }
}
