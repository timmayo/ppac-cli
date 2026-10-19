using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Config;
using Ppac.Core.Http;
using Ppac.Core.Output;

namespace Ppac.Tests;

public class OutputWriterTests
{
    private static List<JsonObject> Rows() =>
    [
        (JsonObject)JsonNode.Parse("""{"name":"a1","properties":{"displayName":"Contoso Prod"},"n":1}""")!,
        (JsonObject)JsonNode.Parse("""{"name":"b22","properties":{"displayName":"Has, comma \"quoted\""}}""")!,
    ];

    private static readonly Column[] Columns = [new("Name", "name"), new("DisplayName", "properties.displayName")];

    private static string Render(OutputFormat format)
    {
        var writer = new StringWriter { NewLine = "\n" };
        OutputWriter.Write(writer, Rows(), Columns, format);
        return writer.ToString();
    }

    [Fact]
    public void Table_aligns_columns()
    {
        var lines = Render(OutputFormat.Table).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Name  DisplayName", lines[0]);
        Assert.Equal("----  -----------", lines[1].Substring(0, 17));
        Assert.Equal("a1    Contoso Prod", lines[2]);
    }

    [Fact]
    public void Csv_quotes_values_that_need_it()
    {
        var lines = Render(OutputFormat.Csv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Name,DisplayName", lines[0]);
        Assert.Equal("a1,Contoso Prod", lines[1]);
        Assert.Equal("b22,\"Has, comma \"\"quoted\"\"\"", lines[2]);
    }

    [Fact]
    public void Tsv_has_one_line_per_row()
    {
        var lines = Render(OutputFormat.Tsv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("a1\tContoso Prod", lines[1]);
    }

    [Fact]
    public void Jsonl_keeps_full_objects_one_per_line()
    {
        var lines = Render(OutputFormat.Jsonl).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"n\":1", lines[0]);
    }

    [Fact]
    public void Json_is_an_array_of_full_objects()
    {
        var array = (JsonArray)JsonNode.Parse(Render(OutputFormat.Json))!;
        Assert.Equal(2, array.Count);
        Assert.Equal(1, (int)array[0]!["n"]!);
    }

    [Fact]
    public void CellText_reads_nested_paths_and_tolerates_missing()
    {
        var row = Rows()[0];
        Assert.Equal("Contoso Prod", OutputWriter.CellText(row, "properties.displayName"));
        Assert.Equal("1", OutputWriter.CellText(row, "n"));
        Assert.Equal("", OutputWriter.CellText(row, "properties.nope.deeper"));
    }

    [Fact]
    public void Unknown_format_is_a_usage_error()
    {
        var ex = Assert.Throws<PpacException>(() => OutputWriter.ParseFormatOrThrow("xml"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }
}

public class PpApiClientTests
{
    private sealed class FakeTokens : ITokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult("tok");
    }

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new(responses);
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(_queue.Dequeue()(request));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (PpApiClient Client, FakeHandler Handler, List<TimeSpan> Delays) Make(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new FakeHandler(responses);
        var delays = new List<TimeSpan>();
        var client = new PpApiClient(new HttpClient(handler), new FakeTokens(),
            (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        return (client, handler, delays);
    }

    [Fact]
    public async Task Sends_bearer_token_to_the_api_host()
    {
        string? auth = null;
        var (client, handler, _) = Make(request =>
        {
            auth = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        await client.SendAsync(HttpMethod.Get, "/environmentmanagement/environments?api-version=2024-10-01");
        Assert.Equal("Bearer tok", auth);
        Assert.Equal("https://api.powerplatform.com/environmentmanagement/environments?api-version=2024-10-01",
            handler.Urls[0]);
    }

    [Fact]
    public async Task Retries_429_and_honors_Retry_After()
    {
        var (client, handler, delays) = Make(
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return r;
            },
            Json(HttpStatusCode.OK, """{"value":[]}"""));

        var response = await client.SendAsync(HttpMethod.Get, "/x");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), delays[0]);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_with_throttled_exit_code()
    {
        var responses = Enumerable.Range(0, PpApiClient.MaxAttempts)
            .Select(_ => Json(HttpStatusCode.TooManyRequests, "{}")).ToArray();
        var (client, handler, _) = Make(responses);

        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Get, "/x"));

        Assert.Equal(ExitCodes.Throttled, ex.ExitCode);
        Assert.Equal(PpApiClient.MaxAttempts, handler.Urls.Count);
    }

    [Theory]
    [InlineData(401, ExitCodes.Auth)]
    [InlineData(403, ExitCodes.Forbidden)]
    [InlineData(404, ExitCodes.NotFound)]
    [InlineData(409, ExitCodes.Conflict)]
    [InlineData(400, ExitCodes.General)]
    public async Task Maps_http_status_to_exit_code(int status, int expectedExit)
    {
        var (client, handler, _) = Make(Json((HttpStatusCode)status, """{"code":"ForbiddenAccess","message":"nope"}"""));

        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Get, "/x"));

        Assert.Equal(expectedExit, ex.ExitCode);
        Assert.Contains("ForbiddenAccess: nope", ex.Message);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Does_not_retry_other_client_errors()
    {
        var (client, handler, delays) = Make(Json(HttpStatusCode.BadRequest, "{}"));
        await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Get, "/x"));
        Assert.Single(handler.Urls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task GetAll_follows_next_links()
    {
        var (client, handler, _) = Make(
            Json(HttpStatusCode.OK,
                """{"value":[{"name":"a"}],"@odata.nextLink":"https://api.powerplatform.com/x?page=2"}"""),
            Json(HttpStatusCode.OK, """{"value":[{"name":"b"}]}"""));

        var items = await client.GetAllAsync("/x");

        Assert.Equal(2, items.Count);
        Assert.Equal("https://api.powerplatform.com/x?page=2", handler.Urls[1]);
    }

    [Fact]
    public async Task Refuses_to_send_credentials_to_another_host()
    {
        var (client, handler, _) = Make();
        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Get, "https://evil.example/x"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task Refuses_plain_http_even_for_the_api_host()
    {
        var (client, _, _) = Make();
        var ex = await Assert.ThrowsAsync<PpacException>(() =>
            client.SendAsync(HttpMethod.Get, "http://api.powerplatform.com/x"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }
}

public class ProfileStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ppac-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Flags_beat_environment_which_beats_saved_profile()
    {
        var store = new ProfileStore(TempDir());
        store.Save(new Profile("saved-tenant", "saved-client", "me@contoso.com"));
        var env = new Dictionary<string, string?> { ["PPAC_TENANT_ID"] = "env-tenant", ["PPAC_CLIENT_ID"] = "env-client" };

        var fromEnv = store.Resolve(null, null, k => env.GetValueOrDefault(k));
        Assert.Equal("env-tenant", fromEnv.TenantId);
        Assert.Equal("env-client", fromEnv.ClientId);

        var fromFlag = store.Resolve("flag-tenant", null, k => env.GetValueOrDefault(k));
        Assert.Equal("flag-tenant", fromFlag.TenantId);
        Assert.Equal("env-client", fromFlag.ClientId);

        var fromSaved = store.Resolve(null, null, _ => null);
        Assert.Equal("saved-tenant", fromSaved.TenantId);
        Assert.Equal("me@contoso.com", fromSaved.Username);
    }

    [Fact]
    public void Missing_tenant_or_client_is_a_usage_error()
    {
        var store = new ProfileStore(TempDir());
        var noTenant = Assert.Throws<PpacException>(() => store.Resolve(null, "c", _ => null));
        Assert.Equal(ExitCodes.Usage, noTenant.ExitCode);
        var noClient = Assert.Throws<PpacException>(() => store.Resolve("t", null, _ => null));
        Assert.Equal(ExitCodes.Usage, noClient.ExitCode);
    }
}
