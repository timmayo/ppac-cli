using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>'environment operation get' and 'environment group operation get' against a fake service.</summary>
[Collection("GlobalState")]
public sealed class OperationCommandTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public OperationCommandTests()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
    }

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private const string EnvironmentOperationBody = """
        {"name":"DisableManagedGovernance","operationId":"op1","status":"Succeeded",
         "startTime":"2026-10-31T02:58:10Z","endTime":"2026-10-31T02:58:25Z",
         "requestedBy":{"displayName":"Tim","userId":"u1","type":"User"},
         "updatedEnvironment":{"environmentId":"e1","displayName":"ppac-test"},
         "stageStatuses":[{"name":"Validate","status":"Succeeded"},{"name":"Apply","status":"Failed","errorDetail":{"code":"Boom","fieldErrors":{"x":{"errorMessages":["bad x"]}}}}],
         "errorDetail":{"code":"Failed","fieldErrors":{"environment":{"errorMessages":["not allowed"],"suggestedValue":"y"}}}}
        """;

    // ---- requests and ID parsing ----

    [Fact]
    public void Requests_use_the_documented_routes_and_api_version()
    {
        var env = OperationApi.EnvironmentOperationRequest(Cloud, "e 1", "op1");
        var group = OperationApi.GroupOperationRequest(Cloud, "op1");

        Assert.Equal(HttpMethod.Get, env.Method);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/e%201/operations/op1?api-version=2024-10-01",
            env.Url);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environmentGroupOperations/op1?api-version=2024-10-01",
            group.Url);
    }

    [Fact]
    public void An_environment_operation_url_gives_both_ids()
    {
        var ok = OperationApi.TryParseEnvironmentOperation(
            "https://api.powerplatform.com/environmentmanagement/environments/c512/operations/adfd?api-version=1",
            out var environmentId, out var operationId);

        Assert.True(ok);
        Assert.Equal("c512", environmentId);
        Assert.Equal("adfd", operationId);
    }

    [Fact]
    public void A_bare_operation_id_has_no_environment()
    {
        Assert.True(OperationApi.TryParseEnvironmentOperation(" adfd ", out var environmentId, out var operationId));
        Assert.Null(environmentId);
        Assert.Equal("adfd", operationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("https://api.powerplatform.com/environmentmanagement/environmentGroupOperations/abc?api-version=1")]
    public void Unusable_environment_operation_values_are_rejected(string value) =>
        Assert.False(OperationApi.TryParseEnvironmentOperation(value, out _, out _));

    [Fact]
    public void A_group_operation_url_gives_the_id()
    {
        Assert.True(OperationApi.TryParseGroupOperation(
            "https://api.powerplatform.com/environmentmanagement/environmentGroupOperations/ba90?api-version=1", out var id));
        Assert.Equal("ba90", id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://api.powerplatform.com/environmentmanagement/environments/e1/operations/op1")]
    public void Unusable_group_operation_values_are_rejected(string value) =>
        Assert.False(OperationApi.TryParseGroupOperation(value, out _));

    // ---- environment operation ----

    [Fact]
    public async Task Environment_operation_text_shows_status_stages_and_errors_and_exits_0_even_when_it_failed()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, EnvironmentOperationBody));

        var exitCode = await OperationCommands.GetEnvironmentOperationCoreAsync(Api(handler), Cloud, "e1", "op1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.Contains("Status:", text);
        Assert.Contains("Succeeded", text);
        Assert.Contains("Requested by:", text);
        Assert.Contains("Tim", text);
        Assert.Contains("ppac-test (e1)", text);
        Assert.Contains("Apply", text);
        Assert.Contains("Boom; x: bad x", text);
        Assert.Contains("Failed; environment: not allowed", text);
        Assert.Contains("/environments/e1/operations/op1?", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Environment_operation_json_returns_the_body_unchanged()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, EnvironmentOperationBody));

        var exitCode = await OperationCommands.GetEnvironmentOperationCoreAsync(Api(handler), Cloud, "e1", "op1", json: true, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Succeeded", JsonNode.Parse(_out.ToString())!["status"]!.ToString());
    }

    [Fact]
    public async Task An_unknown_environment_operation_is_a_404_api_exception_for_the_runner_to_map()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NotFound, ""));

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            OperationCommands.GetEnvironmentOperationCoreAsync(Api(handler), Cloud, "e1", "nope", json: false, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    // ---- group operation ----

    [Fact]
    public async Task Group_operation_204_is_not_found_exit_4()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));

        var exitCode = await OperationCommands.GetGroupOperationCoreAsync(Api(handler), Cloud, "op1", json: true, CancellationToken.None);

        Assert.Equal(ExitCodes.NotFound, exitCode);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal(204, (int)error["status"]!);
    }

    [Fact]
    public async Task Group_operation_with_common_fields_prints_them_and_says_the_shape_is_undocumented()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"operationId\":\"op1\",\"status\":\"Succeeded\"}"));

        var exitCode = await OperationCommands.GetGroupOperationCoreAsync(Api(handler), Cloud, "op1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Status:", _out.ToString());
        Assert.Contains("does not define this response", _err.ToString());
    }

    [Fact]
    public async Task Group_operation_in_the_lifecycle_shape_seen_live_prints_status_and_stages()
    {
        const string body = """
            {"id":"op1","links":{"environment":{"path":"/providers/Microsoft.BusinessAppPlatform/environments/e1"}},
             "typeDisplayName":"Edit","state":{"id":"Succeeded"},"createdDateTime":"2026-10-31T03:00:50Z",
             "lastActionDateTime":"2026-10-31T03:00:57Z","requestedBy":{"id":"u1","type":"User"},
             "stages":[{"id":"Run","name":"Run","state":{"id":"Succeeded"}}]}
            """;
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, body));

        var exitCode = await OperationCommands.GetGroupOperationCoreAsync(Api(handler), Cloud, "op1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.Contains("Status:", text);
        Assert.Contains("Succeeded", text);
        Assert.Contains("Edit", text);
        Assert.Contains("Run", text);
        Assert.Contains("e1", text);
    }

    [Fact]
    public async Task Group_operation_with_an_unknown_shape_prints_the_json()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"state\":\"done\"}"));

        var exitCode = await OperationCommands.GetGroupOperationCoreAsync(Api(handler), Cloud, "op1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("\"state\": \"done\"", _out.ToString());
    }

    // ---- registry ----

    [Fact]
    public void Operation_commands_are_read_only_and_unverified_until_run_live()
    {
        var specs = CommandRegistry.All.Where(s => s.Path.Contains("operation")).ToList();

        Assert.Equal(
            new[] { "environment group operation get", "environment operation get" },
            specs.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(specs, s => Assert.Equal(CommandClass.Read, s.Class));
        Assert.All(specs, s => Assert.Equal(VerificationStatus.Unverified, s.Status));
        Assert.All(specs, s => Assert.False(s.McpEligible, s.Name));
        Assert.All(specs, s => Assert.True(s.Options.Single(o => o.Name == "--operation").Required));
        Assert.DoesNotContain(specs, s => s.Options.Any(o => o.Name == "--whatif" || o.Name == "--yes"));
    }
}
