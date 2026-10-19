using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Http;
using Ppac.Core.Operations;
using Ppac.Core.Output;
using Ppac.Core.Safety;

namespace Ppac.Tests;

internal sealed class StubTokens : ITokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult("tok");
}

internal sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new(responses);
    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Urls.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(_queue.Dequeue()(request));
    }
}

public class RetryRuleTests
{
    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode status, string body = "{}") =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (PpApiClient Client, StubHandler Handler) Make(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new StubHandler(responses);
        var client = new PpApiClient(new HttpClient(handler), new StubTokens(), (_, _) => Task.CompletedTask);
        return (client, handler);
    }

    [Fact]
    public async Task Post_is_not_retried_on_503_because_it_may_have_been_processed()
    {
        var (client, handler) = Make(Respond(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Post, "/x", "{}"));

        Assert.Equal(ExitCodes.General, ex.ExitCode);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Delete_is_not_retried_on_502()
    {
        var (client, handler) = Make(Respond(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Delete, "/x"));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Get_is_retried_on_503()
    {
        var (client, handler) = Make(Respond(HttpStatusCode.ServiceUnavailable), Respond(HttpStatusCode.OK));
        var response = await client.SendAsync(HttpMethod.Get, "/x");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task Post_is_retried_on_429_because_it_was_not_processed()
    {
        var (client, handler) = Make(Respond(HttpStatusCode.TooManyRequests), Respond(HttpStatusCode.Accepted));
        var response = await client.SendAsync(HttpMethod.Post, "/x", "{}");
        Assert.Equal(HttpStatusCode.Accepted, response.Status);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task Response_headers_are_available_case_insensitively()
    {
        var (client, _) = Make(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("") };
            r.Headers.TryAddWithoutValidation("Operation-Location", "https://api.powerplatform.com/ops/1");
            return r;
        });

        var response = await client.SendAsync(HttpMethod.Delete, "/x");

        Assert.Equal("https://api.powerplatform.com/ops/1", response.Headers["operation-location"]);
    }

    [Fact]
    public async Task Validation_errors_use_the_errorDetail_code_in_the_message()
    {
        var (client, _) = Make(Respond(HttpStatusCode.BadRequest, """{"errorDetail":{"code":"EnvironmentNotFound"}}"""));
        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Delete, "/x"));
        Assert.Contains("EnvironmentNotFound", ex.Message);
    }

    [Fact]
    public async Task GetCollection_follows_continuation_tokens()
    {
        var (client, handler) = Make(
            Respond(HttpStatusCode.OK, """{"collection":[{"id":"a"}],"continuationToken":"t 1"}"""),
            Respond(HttpStatusCode.OK, """{"collection":[{"id":"b"}]}"""));

        var items = await client.GetCollectionAsync("/e/1/backups?api-version=2024-10-01");

        Assert.Equal(2, items.Count);
        Assert.Contains("api-version=2024-10-01&continuationToken=t%201", handler.Urls[1]);
    }
}

public class OperationWaiterTests
{
    private static JsonObject Op(string status, string? errorCode = null)
    {
        var op = new JsonObject { ["operationId"] = "op1", ["status"] = status };
        if (errorCode is not null) op["errorDetail"] = new JsonObject { ["code"] = errorCode };
        return op;
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int Delays { get; private set; }
        public Task Delay(TimeSpan span, CancellationToken ct) { Now += span; Delays++; return Task.CompletedTask; }
    }

    private static Func<CancellationToken, Task<JsonObject>> Sequence(params JsonObject[] ops)
    {
        var index = 0;
        return _ => Task.FromResult(ops[Math.Min(index++, ops.Length - 1)]);
    }

    [Fact]
    public async Task Returns_the_operation_once_it_succeeds()
    {
        var clock = new Clock();
        var result = await OperationWaiter.WaitAsync(Sequence(Op("Queued"), Op("InProgress"), Op("Succeeded")),
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), clock.Delay, () => clock.Now, null, default);

        Assert.Equal("Succeeded", OutputWriter.CellText(result, "status"));
        Assert.Equal(2, clock.Delays);
    }

    [Fact]
    public async Task Failed_operation_exits_8_with_the_error_code()
    {
        var clock = new Clock();
        var ex = await Assert.ThrowsAsync<PpacException>(() => OperationWaiter.WaitAsync(
            Sequence(Op("InProgress"), Op("Failed", "CopyFailed")),
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), clock.Delay, () => clock.Now, null, default));

        Assert.Equal(ExitCodes.AsyncFailed, ex.ExitCode);
        Assert.Contains("CopyFailed", ex.Message);
    }

    [Fact]
    public async Task Validation_failed_also_exits_8()
    {
        var clock = new Clock();
        var ex = await Assert.ThrowsAsync<PpacException>(() => OperationWaiter.WaitAsync(
            Sequence(Op("ValidationFailed")),
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), clock.Delay, () => clock.Now, null, default));
        Assert.Equal(ExitCodes.AsyncFailed, ex.ExitCode);
    }

    [Fact]
    public async Task Times_out_with_exit_9_while_still_running()
    {
        var clock = new Clock();
        var ex = await Assert.ThrowsAsync<PpacException>(() => OperationWaiter.WaitAsync(
            Sequence(Op("InProgress")),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35), clock.Delay, () => clock.Now, null, default));

        Assert.Equal(ExitCodes.Timeout, ex.ExitCode);
        Assert.Contains("InProgress", ex.Message);
        Assert.Equal(3, clock.Delays);
    }

    [Fact]
    public async Task Zero_timeout_checks_once_and_then_times_out()
    {
        var clock = new Clock();
        var ex = await Assert.ThrowsAsync<PpacException>(() => OperationWaiter.WaitAsync(
            Sequence(Op("Queued")),
            TimeSpan.FromSeconds(10), TimeSpan.Zero, clock.Delay, () => clock.Now, null, default));
        Assert.Equal(ExitCodes.Timeout, ex.ExitCode);
        Assert.Equal(0, clock.Delays);
    }
}

public class DurationParserTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("45", 45)]
    [InlineData("1.5m", 90)]
    [InlineData(" 5M ", 300)]
    public void Parses_valid_durations(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), DurationParser.Parse(text, "--timeout"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5s")]
    [InlineData("10x")]
    public void Rejects_invalid_durations_as_usage_errors(string text)
    {
        var ex = Assert.Throws<PpacException>(() => DurationParser.Parse(text, "--timeout"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("--timeout", ex.Message);
    }
}

public class ConfirmationTests
{
    [Fact]
    public void Yes_skips_the_prompt_and_never_reads_input()
    {
        var prompt = new StringWriter();
        new Confirmation(new StringReader(""), prompt, interactive: false).Require("delete environment 'Dev'", "Dev", yes: true);
        Assert.Equal("", prompt.ToString());
    }

    [Fact]
    public void Non_interactive_without_yes_exits_2()
    {
        var confirmation = new Confirmation(new StringReader("Dev"), new StringWriter(), interactive: false);
        var ex = Assert.Throws<PpacException>(() => confirmation.Require("delete environment 'Dev'", "Dev", yes: false));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("--yes", ex.Message);
    }

    [Fact]
    public void Interactive_requires_the_exact_name()
    {
        var prompt = new StringWriter();
        new Confirmation(new StringReader("Dev\n"), prompt, interactive: true).Require("delete environment 'Dev'", "Dev", yes: false);
        Assert.Contains("Type 'Dev' to confirm", prompt.ToString());
    }

    [Fact]
    public void Interactive_wrong_or_differently_cased_name_changes_nothing()
    {
        var wrong = new Confirmation(new StringReader("dev\n"), new StringWriter(), interactive: true);
        var ex = Assert.Throws<PpacException>(() => wrong.Require("delete environment 'Dev'", "Dev", yes: false));
        Assert.Equal(ExitCodes.General, ex.ExitCode);
        Assert.Contains("Nothing was changed", ex.Message);
    }

    [Fact]
    public void Interactive_empty_input_changes_nothing()
    {
        var empty = new Confirmation(new StringReader(""), new StringWriter(), interactive: true);
        Assert.Throws<PpacException>(() => empty.Require("delete environment 'Dev'", "Dev", yes: false));
    }
}

public class WriteColumnsTests
{
    // Shapes from Microsoft's reference for EnvironmentBackup and OperationExecutionResult (not live output yet).
    [Fact]
    public void Backup_columns_read_documented_fields()
    {
        var row = (JsonObject)JsonNode.Parse("""
            {"id":"b1","label":"before upgrade","backupPointDateTime":"2026-10-19T10:00:00Z","backupExpiryDateTime":"2026-11-19T10:00:00Z"}
            """)!;
        var values = Columns.Backups.ToDictionary(c => c.Header, c => OutputWriter.CellText(row, c.Path));
        Assert.Equal("before upgrade", values["Label"]);
        Assert.Equal("2026-10-19T10:00:00Z", values["BackupPoint"]);
        Assert.Equal("2026-11-19T10:00:00Z", values["Expires"]);
        Assert.Equal("b1", values["Id"]);
    }

    [Fact]
    public void Operation_columns_read_documented_fields()
    {
        var row = (JsonObject)JsonNode.Parse("""
            {"operationId":"op1","name":"CopyEnvironment","status":"InProgress","startTime":"2026-10-19T10:00:00Z"}
            """)!;
        var values = Columns.Operations.ToDictionary(c => c.Header, c => OutputWriter.CellText(row, c.Path));
        Assert.Equal("CopyEnvironment", values["Name"]);
        Assert.Equal("InProgress", values["Status"]);
        Assert.Equal("", values["Ended"]);
        Assert.Equal("op1", values["OperationId"]);
    }
}