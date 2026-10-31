using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Safety;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>Group create/delete behavior, including --whatif, --yes and the confirmation prompt.</summary>
[Collection("GlobalState")]
public sealed class EnvironmentGroupWriteCommandTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "ppac-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("PPAC_HOME");
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly Func<bool> _previousInteractive = WriteSafety.IsInteractive;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public EnvironmentGroupWriteCommandTests()
    {
        Environment.SetEnvironmentVariable("PPAC_HOME", _home);
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
        WriteSafety.IsInteractive = _previousInteractive;
        Environment.SetEnvironmentVariable("PPAC_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private const string GroupBody =
        "{\"id\":\"g1\",\"displayName\":\"Test Zone\",\"createdTime\":\"2026-10-30T01:00:00Z\"}";

    /// <summary>Fake service: get returns the group (or the override), delete and create succeed.</summary>
    private static FakeHandler Service(HttpStatusCode getStatus = HttpStatusCode.OK, string? getBody = null) => new(request =>
    {
        if (request.Method == HttpMethod.Delete) return (HttpStatusCode.NoContent, "");
        if (request.Method == HttpMethod.Post) return (HttpStatusCode.OK, GroupBody);
        return (getStatus, getStatus == HttpStatusCode.NoContent ? "" : getBody ?? GroupBody);
    });

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private static int DeleteCount(FakeHandler handler) => handler.Requests.Count(r => r.Method == HttpMethod.Delete);

    private Task<int> Delete(FakeHandler handler, bool whatIf = false, bool yes = false, bool json = false,
        bool interactive = false, string answer = "") =>
        EnvironmentGroupWriteCommands.DeleteCoreAsync(
            Api(handler), Cloud, "g1", whatIf, yes, json, interactive, new StringReader(answer), CancellationToken.None);

    // A missing required option is a parse error; Program.cs turns it into exit code 2.
    private static bool ParseFails(params string[] args) =>
        CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions).Parse(args).Errors.Count > 0;

    private static async Task<int> RunCli(params string[] args)
    {
        var root = CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);
        return await root.Parse(args).InvokeAsync();
    }

    // ---- request shapes ----

    [Fact]
    public void Create_request_posts_display_name_and_description_only()
    {
        var request = EnvironmentGroupApi.CreateRequest(Cloud, "Zone A", "For tests");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environmentGroups?api-version=2024-10-01",
            request.Url);
        Assert.Equal("{\"displayName\":\"Zone A\",\"description\":\"For tests\"}", request.Body!.ToJsonString());
    }

    [Fact]
    public void Delete_request_targets_the_group_id()
    {
        var request = EnvironmentGroupApi.DeleteRequest(Cloud, "a b");

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environmentGroups/a%20b?api-version=2024-10-01",
            request.Url);
        Assert.Null(request.Body);
    }

    // ---- create ----

    [Fact]
    public async Task Create_whatif_is_local_and_prints_the_request()
    {
        var exitCode = await RunCli("environment", "group", "create", "--name", "Zone A", "--description", "For tests", "--whatif");

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.Contains("What if: no changes were made.", text);
        Assert.Contains("Request: POST https://", text);
        Assert.Contains("Body: {\"displayName\":\"Zone A\",\"description\":\"For tests\"}", text);
        Assert.Contains("checks that no existing group has this name", text);
    }

    [Fact]
    public async Task Create_without_a_name_or_description_is_a_usage_error()
    {
        Assert.True(ParseFails("environment", "group", "create"));
        Assert.True(ParseFails("environment", "group", "create", "--name", "Zone A"));
        Assert.True(ParseFails("environment", "group", "create", "--description", "For tests"));
        Assert.Equal(ExitCodes.Usage, await RunCli("environment", "group", "create", "--name", " ", "--description", "d"));
        Assert.Equal(ExitCodes.Usage, await RunCli("environment", "group", "create", "--name", "Zone A", "--description", " "));
    }

    [Fact]
    public async Task Create_without_whatif_needs_a_profile()
    {
        var exitCode = await RunCli("environment", "group", "create", "--name", "Zone A", "--description", "d");

        Assert.Equal(ExitCodes.AuthRequired, exitCode);
        Assert.Contains("No active profile", _err.ToString());
    }

    [Fact]
    public async Task Create_prints_the_new_group()
    {
        var handler = Service();

        var exitCode = await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(handler), Cloud, "Test Zone", "d", allowDuplicate: false, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Group created.", _out.ToString());
        Assert.Contains("g1", _out.ToString());
        Assert.Equal("{\"displayName\":\"Test Zone\",\"description\":\"d\"}", handler.Bodies.Single(b => b is not null));
    }

    [Fact]
    public async Task Create_json_returns_the_group_and_a_204_becomes_created_true()
    {
        await EnvironmentGroupWriteCommands.CreateCoreAsync(Api(Service()), Cloud, "Test Zone", "d", allowDuplicate: false, json: true, CancellationToken.None);
        Assert.Equal("g1", JsonNode.Parse(_out.ToString())!["id"]!.ToString());

        _out.GetStringBuilder().Clear();
        var noContent = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));
        await EnvironmentGroupWriteCommands.CreateCoreAsync(Api(noContent), Cloud, "Test Zone", "d", allowDuplicate: false, json: true, CancellationToken.None);
        Assert.True((bool)JsonNode.Parse(_out.ToString())!["created"]!);
    }

    [Fact]
    public async Task Create_with_no_content_says_so_in_text_mode()
    {
        var noContent = new FakeHandler(_ => (HttpStatusCode.NoContent, ""));

        var exitCode = await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(noContent), Cloud, "Test Zone", "d", allowDuplicate: false, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("returned no details", _out.ToString());
    }

    // ---- create: duplicate names (the service allows them, seen live) ----

    private static FakeHandler ServiceWithExistingGroup(string existingName, string existingId = "g9") => new(request =>
    {
        if (request.Method == HttpMethod.Post) return (HttpStatusCode.OK, GroupBody);
        return (HttpStatusCode.OK, $"{{\"value\":[{{\"id\":\"{existingId}\",\"displayName\":\"{existingName}\"}}]}}");
    });

    private static int Posts(FakeHandler handler) => handler.Requests.Count(r => r.Method == HttpMethod.Post);

    [Theory]
    [InlineData("Test Zone")]
    [InlineData("test zone")]
    [InlineData("  TEST ZONE ")]
    public async Task Create_refuses_a_name_another_group_already_has_and_sends_no_post(string existing)
    {
        var handler = ServiceWithExistingGroup(existing);

        var exitCode = await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(handler), Cloud, "Test Zone", "d", allowDuplicate: false, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("already exists (ID g9)", _err.ToString());
        Assert.Contains("--allow-duplicate", _err.ToString());
    }

    [Fact]
    public async Task Create_duplicate_refusal_json_lists_the_existing_ids()
    {
        var handler = ServiceWithExistingGroup("Test Zone");

        await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(handler), Cloud, "Test Zone", "d", allowDuplicate: false, json: true, CancellationToken.None);

        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal(1, (int)error["exitCode"]!);
        Assert.True((bool)error["details"]!["duplicate"]!);
        Assert.Equal("g9", error["details"]!["existingGroupIds"]![0]!.ToString());
    }

    [Fact]
    public async Task Create_with_allow_duplicate_skips_the_name_check_and_posts()
    {
        var handler = ServiceWithExistingGroup("Test Zone");

        var exitCode = await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(handler), Cloud, "Test Zone", "d", allowDuplicate: true, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Create_with_a_different_name_posts_after_checking_the_list()
    {
        var handler = ServiceWithExistingGroup("Other Zone");

        var exitCode = await EnvironmentGroupWriteCommands.CreateCoreAsync(
            Api(handler), Cloud, "Test Zone", "d", allowDuplicate: false, json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_whatif_looks_the_group_up_sends_no_delete_and_names_it()
    {
        var handler = Service();

        var exitCode = await Delete(handler, whatIf: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        var text = _out.ToString();
        Assert.Contains("What if: no changes were made.", text);
        Assert.Contains("group 'Test Zone' (g1)", text);
        Assert.Contains("Request: DELETE https://", text);
        Assert.Contains("holds no environments", text);
    }

    /// <summary>Fake service where group g1 holds one environment (or the membership filter is rejected).</summary>
    private static FakeHandler ServiceWithMember(bool filterRejected = false) => new(request =>
    {
        if (request.Method == HttpMethod.Delete) return (HttpStatusCode.NoContent, "");
        if (request.RequestUri!.AbsoluteUri.Contains("/environments?"))
        {
            return filterRejected
                ? (HttpStatusCode.BadRequest, "{\"title\":\"Bad Request\"}")
                : (HttpStatusCode.OK, "{\"value\":[{\"id\":\"e1\",\"displayName\":\"ppac-test\"}]}");
        }
        return (HttpStatusCode.OK, GroupBody);
    });

    [Fact]
    public async Task Delete_refuses_a_group_that_holds_environments_and_says_how_to_fix_it()
    {
        var handler = ServiceWithMember();

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        var text = _err.ToString();
        Assert.Contains("still holds 1 environment(s): ppac-test (e1)", text);
        Assert.Contains("ppac environment group remove-environment --group g1 --environment e1", text);
    }

    [Fact]
    public async Task Delete_refusal_json_lists_the_environments()
    {
        var handler = ServiceWithMember();

        await Delete(handler, yes: true, json: true);

        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal(1, (int)error["exitCode"]!);
        Assert.True((bool)error["details"]!["groupHasEnvironments"]!);
        Assert.Equal("e1", error["details"]!["environments"]![0]!["id"]!.ToString());
        Assert.Contains("remove-environment", error["hint"]!.ToString());
    }

    [Fact]
    public async Task Delete_whatif_names_the_environments_the_group_holds_and_sends_no_delete()
    {
        var handler = ServiceWithMember();

        var exitCode = await Delete(handler, whatIf: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("still holds 1 environment(s): ppac-test (e1)", _out.ToString());
    }

    [Fact]
    public async Task Delete_goes_ahead_when_the_membership_filter_is_rejected_and_lets_the_service_decide()
    {
        var handler = ServiceWithMember(filterRejected: true);

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, DeleteCount(handler));
    }

    [Fact]
    public async Task Delete_refuses_when_only_one_of_the_membership_reads_shows_an_environment()
    {
        // Live, the filter missed a member in 6 of 20 reads, so one empty read must not clear the group.
        var reads = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Delete) return (HttpStatusCode.NoContent, "");
            if (request.RequestUri!.AbsoluteUri.Contains("/environments?"))
            {
                reads++;
                return reads == 5
                    ? (HttpStatusCode.OK, "{\"value\":[{\"id\":\"e1\",\"displayName\":\"ppac-test\"}]}")
                    : (HttpStatusCode.OK, "{\"value\":[]}");
            }
            return (HttpStatusCode.OK, GroupBody);
        });

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("ppac-test (e1)", _err.ToString());
    }

    [Fact]
    public async Task Delete_explains_the_services_conflict_refusal_when_the_reads_showed_no_environment()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                return (HttpStatusCode.BadRequest, "{\"detail\":\"Request failed with status code Conflict for correlationId abc\"}");
            }
            if (request.RequestUri!.AbsoluteUri.Contains("/environments?")) return (HttpStatusCode.OK, "{\"value\":[]}");
            return (HttpStatusCode.OK, GroupBody);
        });

        var exitCode = await Delete(handler, yes: true, json: true);

        Assert.Equal(ExitCodes.Failure, exitCode);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.True((bool)error["details"]!["refusedByService"]!);
        Assert.Contains("still holds an environment", error["detail"]!.ToString());
        Assert.Contains("environmentGroupId eq 'g1'", error["hint"]!.ToString());
    }

    [Fact]
    public async Task Delete_whatif_json_is_one_object()
    {
        var exitCode = await Delete(Service(), whatIf: true, json: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        var result = JsonNode.Parse(_out.ToString())!;
        Assert.True((bool)result["whatIf"]!);
        Assert.Equal("found", result["details"]!["lookup"]!.ToString());
    }

    [Fact]
    public async Task Delete_with_yes_deletes_without_a_prompt()
    {
        var handler = Service();

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, DeleteCount(handler));
        Assert.DoesNotContain("Continue?", _err.ToString());
        Assert.Contains("Deleted group 'Test Zone' (g1)", _out.ToString());
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task Delete_prompt_treats_anything_but_yes_as_no_and_exits_6(string answer)
    {
        var handler = Service();

        var exitCode = await Delete(handler, interactive: true, answer: answer);

        Assert.Equal(ExitCodes.Declined, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("'Test Zone'", _err.ToString());
        Assert.Contains("Continue? [y/N]", _err.ToString());
    }

    [Theory]
    [InlineData("y")]
    [InlineData("YES")]
    public async Task Delete_prompt_accepts_y_or_yes(string answer)
    {
        var handler = Service();

        Assert.Equal(ExitCodes.Success, await Delete(handler, interactive: true, answer: answer));
        Assert.Equal(1, DeleteCount(handler));
    }

    [Fact]
    public async Task Delete_refuses_when_not_interactive_and_no_yes()
    {
        var handler = Service();

        var exitCode = await Delete(handler, interactive: false);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("--yes", _err.ToString());
    }

    [Fact]
    public async Task Delete_command_refuses_before_signing_in_when_not_interactive_and_no_yes()
    {
        WriteSafety.IsInteractive = () => false;

        var exitCode = await RunCli("environment", "group", "delete", "--group", "g1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("No active profile", _err.ToString());
    }

    [Fact]
    public async Task Delete_command_with_yes_then_needs_a_profile()
    {
        WriteSafety.IsInteractive = () => false;

        var exitCode = await RunCli("environment", "group", "delete", "--group", "g1", "--yes");

        Assert.Equal(ExitCodes.AuthRequired, exitCode);
    }

    [Fact]
    public async Task Delete_of_an_unknown_group_exits_4_and_sends_no_delete()
    {
        var handler = Service(HttpStatusCode.NoContent);

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.NotFound, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("was not found", _err.ToString());
    }

    [Fact]
    public async Task Delete_of_an_unknown_group_reports_a_json_error()
    {
        var exitCode = await Delete(Service(HttpStatusCode.NoContent), whatIf: true, json: true);

        Assert.Equal(ExitCodes.NotFound, exitCode);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal(4, (int)error["exitCode"]!);
        Assert.Equal("notFound", error["details"]!["lookup"]!.ToString());
    }

    [Fact]
    public void Delete_command_requires_group() =>
        Assert.True(ParseFails("environment", "group", "delete"));
}
