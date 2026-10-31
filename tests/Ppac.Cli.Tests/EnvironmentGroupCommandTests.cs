using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>Environment group command behavior. Changes Console output and PPAC_HOME, so it runs with the other global-state tests.</summary>
[Collection("GlobalState")]
public sealed class EnvironmentGroupCommandTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "ppac-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("PPAC_HOME");
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public EnvironmentGroupCommandTests()
    {
        Environment.SetEnvironmentVariable("PPAC_HOME", _home);
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
        Environment.SetEnvironmentVariable("PPAC_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private const string TwoGroups =
        "{\"value\":[" +
        "{\"id\":\"g2\",\"displayName\":\"Production\",\"description\":\"Live apps\",\"lastModifiedTime\":\"2026-10-01T00:00:00Z\"}," +
        "{\"id\":\"g1\",\"displayName\":\"Dev\",\"parentGroupId\":\"g2\"}]}";

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private static async Task<int> RunCli(params string[] args)
    {
        var root = CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);
        return await root.Parse(args).InvokeAsync();
    }

    [Fact]
    public async Task List_prints_a_table_sorted_by_name()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, TwoGroups));

        var exitCode = await EnvironmentGroupCommands.ListCoreAsync(Api(handler), Cloud, null, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.True(text.IndexOf("Dev", StringComparison.Ordinal) < text.IndexOf("Production", StringComparison.Ordinal));
        Assert.Contains("2 group(s).", text);
    }

    [Fact]
    public async Task List_table_shows_created_and_only_shows_the_parent_column_when_a_group_has_a_parent()
    {
        // Shape seen live on 2026-10-30: createdTime and createdBy{id,type} only; no parent or last-modified.
        var live = new FakeHandler(_ => (HttpStatusCode.OK,
            "{\"value\":[{\"id\":\"g1\",\"displayName\":\"Green Zone\",\"description\":\"Green Zone\"," +
            "\"createdTime\":\"2025-12-03T16:31:06.7798965Z\",\"createdBy\":{\"id\":\"u1\",\"type\":\"User\"}}]}"));
        await EnvironmentGroupCommands.ListCoreAsync(Api(live), Cloud, null, json: false, CancellationToken.None);
        var text = _out.ToString();
        Assert.Contains("Created", text);
        Assert.Contains("2025-12-03T16:31:06.7798965Z", text);
        Assert.DoesNotContain("Parent group ID", text);
        Assert.DoesNotContain("Last modified", text);

        _out.GetStringBuilder().Clear();
        var nested = new FakeHandler(_ => (HttpStatusCode.OK, TwoGroups));
        await EnvironmentGroupCommands.ListCoreAsync(Api(nested), Cloud, null, json: false, CancellationToken.None);
        Assert.Contains("Parent group ID", _out.ToString());
    }

    [Fact]
    public async Task List_json_keeps_the_service_shape_and_empty_says_so()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, TwoGroups));
        await EnvironmentGroupCommands.ListCoreAsync(Api(handler), Cloud, null, json: true, CancellationToken.None);
        Assert.Equal(2, JsonNode.Parse(_out.ToString())!["value"]!.AsArray().Count);

        var empty = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));
        _out.GetStringBuilder().Clear();
        await EnvironmentGroupCommands.ListCoreAsync(Api(empty), Cloud, null, json: false, CancellationToken.None);
        Assert.Contains("No environment groups found.", _out.ToString());
    }

    [Fact]
    public async Task Get_without_a_group_lists_the_first_10_instead_of_failing()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, TwoGroups));

        var exitCode = await EnvironmentGroupCommands.GetCoreAsync(Api(handler), Cloud, null, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Production", _out.ToString());
        Assert.Contains("--group <id>", _err.ToString());
        Assert.EndsWith("environmentGroups?api-version=2024-10-01", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Get_with_a_group_prints_the_details_and_falls_back_through_the_principal_fields()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            "{\"id\":\"g1\",\"displayName\":\"Dev\",\"childrenGroupIds\":[\"c1\",\"c2\"]," +
            "\"createdBy\":{\"userPrincipalName\":\"tim@contoso.test\",\"id\":\"u1\"},\"lastModifiedBy\":{\"id\":\"u2\"}}"));

        var exitCode = await EnvironmentGroupCommands.GetCoreAsync(Api(handler), Cloud, "g1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.Contains("Dev", text);
        Assert.Contains("c1, c2", text);
        Assert.Contains("tim@contoso.test", text);
        Assert.Contains("u2", text);
    }

    [Fact]
    public async Task Get_with_a_group_json_returns_the_group_as_is()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"id\":\"g1\",\"displayName\":\"Dev\"}"));

        await EnvironmentGroupCommands.GetCoreAsync(Api(handler), Cloud, "g1", json: true, CancellationToken.None);

        Assert.Equal("Dev", JsonNode.Parse(_out.ToString())!["displayName"]!.ToString());
    }

    [Fact]
    public async Task Get_with_a_group_that_returns_no_content_fails_with_exit_code_4()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));

        var exitCode = await EnvironmentGroupCommands.GetCoreAsync(Api(handler), Cloud, "g1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.NotFound, exitCode);
        Assert.Contains("no content", _err.ToString());
    }

    [Fact]
    public async Task Commands_run_without_arguments_so_they_only_fail_for_a_missing_profile_not_for_usage()
    {
        Assert.Equal(ExitCodes.AuthRequired, await RunCli("environment", "group", "list"));
        Assert.Contains("No active profile", _err.ToString());

        Assert.Equal(ExitCodes.AuthRequired, await RunCli("environment", "group", "get"));
    }

    [Fact]
    public async Task Bad_values_exit_with_code_2()
    {
        Assert.Equal(ExitCodes.Usage, await RunCli("environment", "group", "list", "--top", "0"));
        Assert.Equal(ExitCodes.Usage, await RunCli("environment", "group", "get", "--group", " "));
    }
}
