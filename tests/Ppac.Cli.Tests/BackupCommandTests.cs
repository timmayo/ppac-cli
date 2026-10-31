using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Safety;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>
/// Backup command behavior, including --whatif, --yes and the confirmation prompt.
/// Changes Console output, PPAC_HOME and the interactive check, so it runs with the
/// other global-state tests, one at a time.
/// </summary>
[Collection("GlobalState")]
public sealed class BackupCommandTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "ppac-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("PPAC_HOME");
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly Func<bool> _previousInteractive = WriteSafety.IsInteractive;
    private StringWriter _out = new();
    private StringWriter _err = new();

    public BackupCommandTests()
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

    private const string ListBody =
        "{\"collection\":[{\"id\":\"b1\",\"label\":\"nightly\",\"backupPointDateTime\":\"2026-10-30T01:00:00Z\"," +
        "\"backupExpiryDateTime\":\"2026-11-30T01:00:00Z\",\"createdBy\":{\"displayName\":\"Tim\"}}]}";

    /// <summary>A fake service: list returns ListBody (or the override), environment get names the environment.</summary>
    private static FakeHandler Service(string? listBody = null) => new(request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (request.Method == HttpMethod.Delete)
        {
            return (HttpStatusCode.NoContent, "");
        }
        if (request.Method == HttpMethod.Post)
        {
            return (HttpStatusCode.Created, "{\"id\":\"b2\",\"label\":\"x\",\"backupPointDateTime\":\"2026-10-31T00:00:00Z\"}");
        }
        return url.Contains("/backups?")
            ? (HttpStatusCode.OK, listBody ?? ListBody)
            : (HttpStatusCode.OK, "{\"displayName\":\"ppac-test2\",\"type\":\"Sandbox\"}");
    });

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private static int DeleteCount(FakeHandler handler) =>
        handler.Requests.Count(r => r.Method == HttpMethod.Delete);

    private Task<int> Delete(
        FakeHandler handler, bool whatIf = false, bool yes = false, bool json = false,
        bool interactive = false, string answer = "") =>
        BackupCommands.DeleteCoreAsync(
            Api(handler), Cloud, "env1", "b1", whatIf, yes, json, interactive,
            new StringReader(answer), CancellationToken.None);

    private async Task<int> RunCli(params string[] args)
    {
        var root = CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);
        return await root.Parse(args).InvokeAsync();
    }

    // ---- delete: --whatif ----

    [Fact]
    public async Task Delete_whatif_sends_no_delete_and_names_what_would_be_removed()
    {
        var handler = Service();

        var exitCode = await Delete(handler, whatIf: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        var text = _out.ToString();
        Assert.Contains("What if", text);
        Assert.Contains("'nightly'", text);
        Assert.Contains("'ppac-test2'", text);
        Assert.Contains("DELETE https://api.powerplatform.com/environmentmanagement/environments/env1/backups/b1", text);
    }

    [Fact]
    public async Task Delete_whatif_json_describes_the_request_and_the_backup()
    {
        var handler = Service();

        var exitCode = await Delete(handler, whatIf: true, json: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        var json = JsonNode.Parse(_out.ToString())!;
        Assert.True(json["whatIf"]!.GetValue<bool>());
        Assert.Equal("DELETE", json["request"]!["method"]!.ToString());
        Assert.Equal("found", json["details"]!["lookup"]!.ToString());
        Assert.Equal("nightly", json["details"]!["backup"]!["label"]!.ToString());
    }

    [Fact]
    public async Task Delete_whatif_wins_over_yes_and_never_prompts()
    {
        var handler = Service();

        var exitCode = await Delete(handler, whatIf: true, yes: true, interactive: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.DoesNotContain("Continue?", _err.ToString());
    }

    // ---- delete: confirmation and --yes ----

    [Fact]
    public async Task Delete_with_yes_deletes_without_prompting()
    {
        var handler = Service();

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, DeleteCount(handler));
        Assert.DoesNotContain("Continue?", _err.ToString());
        Assert.Contains("Deleted backup 'nightly' (b1)", _out.ToString());
    }

    [Fact]
    public async Task Delete_prompt_shows_the_environment_and_a_no_answer_cancels()
    {
        var handler = Service();

        var exitCode = await Delete(handler, interactive: true, answer: "n");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        var prompt = _err.ToString();
        Assert.Contains("'ppac-test2'", prompt);
        Assert.Contains("'nightly'", prompt);
        Assert.Contains("Continue? [y/N]", prompt);
        Assert.Contains("Cancelled", prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task Delete_prompt_treats_anything_but_yes_as_no(string answer)
    {
        var handler = Service();

        Assert.Equal(ExitCodes.Failure, await Delete(handler, interactive: true, answer: answer));
        Assert.Equal(0, DeleteCount(handler));
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
    public async Task Delete_refuses_when_confirmation_is_needed_but_input_is_not_interactive()
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

        var exitCode = await RunCli("environment", "backup", "delete", "--environment", "env1", "--backup", "b1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--yes", _err.ToString());
        Assert.DoesNotContain("No active profile", _err.ToString());
    }

    [Fact]
    public async Task Delete_command_with_yes_gets_past_the_gate_and_then_needs_a_profile()
    {
        WriteSafety.IsInteractive = () => false;

        var exitCode = await RunCli("environment", "backup", "delete", "--environment", "env1", "--backup", "b1", "--yes");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("No active profile", _err.ToString());
    }

    // ---- delete: lookup ----

    [Fact]
    public async Task Delete_of_an_unknown_backup_sends_no_delete()
    {
        var handler = Service("{\"collection\":[]}");

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Contains("was not found", _err.ToString());
    }

    [Fact]
    public async Task Delete_whatif_of_an_unknown_backup_reports_it_in_json_and_fails()
    {
        var handler = Service("{\"collection\":[]}");

        var exitCode = await Delete(handler, whatIf: true, json: true);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal("notFound", JsonNode.Parse(_out.ToString())!["details"]!["lookup"]!.ToString());
    }

    [Fact]
    public async Task Delete_still_runs_when_the_list_is_incomplete_because_not_found_is_not_conclusive()
    {
        var handler = Service("{\"collection\":[],\"continuationToken\":\"next\"}");

        var exitCode = await Delete(handler, yes: true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, DeleteCount(handler));
    }

    // ---- create ----

    [Fact]
    public async Task Create_posts_the_label_and_prints_the_backup()
    {
        var handler = Service();

        var exitCode = await BackupCommands.CreateCoreAsync(
            Api(handler), Cloud, "env1", "x", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/env1/backups?api-version=2024-10-01",
            post.RequestUri!.AbsoluteUri);
        Assert.Equal("{\"label\":\"x\"}", handler.Bodies[handler.Requests.IndexOf(post)]);
        Assert.Contains("Backup created.", _out.ToString());
        Assert.Contains("b2", _out.ToString());
    }

    [Fact]
    public async Task Create_whatif_needs_no_profile_and_prints_the_exact_request()
    {
        var exitCode = await RunCli(
            "environment", "backup", "create", "--environment", "env1", "--label", "x", "--whatif", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var json = JsonNode.Parse(_out.ToString())!;
        Assert.True(json["whatIf"]!.GetValue<bool>());
        Assert.Equal("POST", json["request"]!["method"]!.ToString());
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/env1/backups?api-version=2024-10-01",
            json["request"]!["url"]!.ToString());
        Assert.Equal("x", json["request"]!["body"]!["label"]!.ToString());
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public async Task Create_whatif_text_shows_method_url_and_body()
    {
        var exitCode = await RunCli(
            "environment", "backup", "create", "--environment", "env1", "--label", "x", "--whatif");

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = _out.ToString();
        Assert.Contains("What if: no changes were made.", text);
        Assert.Contains("Request: POST https://", text);
        Assert.Contains("Body: {\"label\":\"x\"}", text);
    }

    [Fact]
    public async Task Create_without_whatif_needs_a_profile()
    {
        var exitCode = await RunCli("environment", "backup", "create", "--environment", "env1", "--label", "x");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("No active profile", _err.ToString());
    }

    [Fact]
    public async Task Create_rejects_a_blank_label_and_a_blank_environment_with_exit_code_2()
    {
        Assert.Equal(ExitCodes.Usage,
            await RunCli("environment", "backup", "create", "--environment", "env1", "--label", " "));
        Assert.Equal(ExitCodes.Usage,
            await RunCli("environment", "backup", "create", "--environment", " ", "--label", "x", "--whatif"));
    }

    // ---- list ----

    [Fact]
    public async Task List_prints_a_table_and_warns_about_a_continuation_token()
    {
        var handler = Service("{\"collection\":[{\"id\":\"b1\",\"label\":\"nightly\"}],\"continuationToken\":\"next\"}");

        var exitCode = await BackupCommands.ListCoreAsync(Api(handler), Cloud, "env1", json: false, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("nightly", _out.ToString());
        Assert.Contains("1 backup(s).", _out.ToString());
        Assert.Contains("continuation token", _err.ToString());
    }

    [Fact]
    public async Task List_table_keeps_columns_apart_with_real_timestamps_and_shows_the_user_id_when_there_is_no_name()
    {
        // Shape seen live on 2026-10-30: 7-digit fractional seconds, createdBy has only userId.
        var handler = Service(
            "{\"collection\":[{\"id\":\"ac58c9b2-b913-4eda-ad82-04275396fe1c\",\"label\":\"ppac-cli-live-test\"," +
            "\"backupPointDateTime\":\"2026-10-31T00:36:52.256174+00:00\",\"backupExpiryDateTime\":\"2026-11-07T00:36:52.256174+00:00\"," +
            "\"createdBy\":{\"userId\":\"ef5c8d9d-8f04-45b2-b3ee-354279868e34\"}}]}");

        await BackupCommands.ListCoreAsync(Api(handler), Cloud, "env1", json: false, CancellationToken.None);

        var row = _out.ToString().Split('\n').Single(l => l.Contains("ppac-cli-live-test"));
        Assert.DoesNotContain("+00:002026", row);
        Assert.Contains("+00:00 ", row);
        Assert.Contains("ef5c8d9d-8f04-45b2-b3ee-354279868e34", row);
    }

    [Fact]
    public async Task List_json_keeps_the_service_shape()
    {
        var handler = Service();

        await BackupCommands.ListCoreAsync(Api(handler), Cloud, "env1", json: true, CancellationToken.None);

        var json = JsonNode.Parse(_out.ToString())!;
        Assert.Equal("b1", json["collection"]![0]!["id"]!.ToString());
        Assert.Null(json["continuationToken"]);
    }

    [Fact]
    public async Task List_with_no_backups_says_so()
    {
        var handler = Service("{\"collection\":[]}");

        await BackupCommands.ListCoreAsync(Api(handler), Cloud, "env1", json: false, CancellationToken.None);

        Assert.Contains("No backups found.", _out.ToString());
    }
}
