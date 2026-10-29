using System.Text.Json.Nodes;
using Ppac.Cli.Auth;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Tests;

/// <summary>
/// Tests that change process-wide state (PPAC_HOME, Console output). Collection members
/// run one at a time, and each test uses its own temporary PPAC_HOME.
/// </summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public class GlobalStateCollection { }

[Collection("GlobalState")]
public sealed class GlobalStateTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ppac-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("PPAC_HOME");
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;

    public GlobalStateTests()
    {
        Environment.SetEnvironmentVariable("PPAC_HOME", _home);
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

    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        var root = CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);
        var exitCode = await root.Parse(args).InvokeAsync();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static Profile SampleProfile(string name) =>
        new(name, "tenant-id", "client-id", "Commercial", "user@contoso.test", "home-account");

    [Fact]
    public void Profile_store_round_trips_and_selects_the_latest_profile()
    {
        Assert.Null(ProfileStore.GetActive());

        ProfileStore.Upsert(SampleProfile("one"));
        ProfileStore.Upsert(SampleProfile("two"));

        Assert.Equal("two", ProfileStore.GetActive()?.Name);
        Assert.Equal(2, ProfileStore.Load().Profiles.Count);
    }

    [Fact]
    public void Profile_store_replaces_a_profile_with_the_same_name_ignoring_case()
    {
        ProfileStore.Upsert(SampleProfile("Demo"));
        ProfileStore.Upsert(SampleProfile("demo"));

        var file = ProfileStore.Load();
        Assert.Single(file.Profiles);
        Assert.Equal("demo", file.Active);
    }

    [Fact]
    public void Profile_file_contains_no_token_or_secret_fields()
    {
        ProfileStore.Upsert(SampleProfile("demo"));

        var text = File.ReadAllText(AppPaths.ProfilesFile).ToLowerInvariant();
        Assert.DoesNotContain("token", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("password", text);
    }

    [Fact]
    public async Task Auth_who_without_a_profile_fails_with_exit_code_1()
    {
        var (exitCode, _, err) = await RunAsync("auth", "who");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("No active profile", err);
    }

    [Fact]
    public async Task Environment_list_and_get_without_a_profile_fail_with_exit_code_1()
    {
        Assert.Equal(ExitCodes.Failure, (await RunAsync("environment", "list")).ExitCode);
        Assert.Equal(ExitCodes.Failure, (await RunAsync("environment", "get", "--environment", "abc")).ExitCode);
        Assert.Equal(ExitCodes.Failure, (await RunAsync("environment", "get")).ExitCode);
    }

    [Fact]
    public async Task Environment_get_rejects_select_without_environment_with_exit_code_2()
    {
        var (exitCode, _, err) = await RunAsync("environment", "get", "--select", "displayName");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--select", err);
    }

    [Fact]
    public async Task Environment_list_rejects_top_below_1_with_exit_code_2()
    {
        var (exitCode, _, err) = await RunAsync("environment", "list", "--top", "0");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--top", err);
    }

    [Fact]
    public async Task Auth_create_requires_device_code_and_validates_the_name()
    {
        var noDeviceCode = await RunAsync("auth", "create", "--tenant", "t", "--applicationId", "a");
        Assert.Equal(ExitCodes.Usage, noDeviceCode.ExitCode);

        var longName = await RunAsync(
            "auth", "create", "--deviceCode", "--tenant", "t", "--applicationId", "a",
            "--name", new string('n', 31));
        Assert.Equal(ExitCodes.Usage, longName.ExitCode);
    }

    [Fact]
    public async Task Auth_list_json_is_valid_and_marks_the_active_profile()
    {
        ProfileStore.Upsert(SampleProfile("one"));
        ProfileStore.Upsert(SampleProfile("two"));

        var (exitCode, output, _) = await RunAsync("auth", "list", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var profiles = JsonNode.Parse(output)!["profiles"]!.AsArray();
        Assert.Equal(2, profiles.Count);
        Assert.Equal("two", profiles.Single(p => p!["active"]!.GetValue<bool>())!["name"]!.ToString());
    }

    [Fact]
    public async Task Auth_who_json_returns_the_active_profile()
    {
        ProfileStore.Upsert(SampleProfile("demo"));

        var (exitCode, output, _) = await RunAsync("auth", "who", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("demo", json["profile"]!.ToString());
        Assert.Equal("tenant-id", json["tenantId"]!.ToString());
    }

    [Fact]
    public async Task Commands_json_lists_every_registry_command_with_required_fields()
    {
        var (exitCode, output, _) = await RunAsync("commands", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("ppac", json["tool"]!.ToString());
        Assert.NotNull(json["exitCodes"]);

        var commands = json["commands"]!.AsArray();
        Assert.Equal(CommandRegistry.All.Count, commands.Count);
        foreach (var command in commands)
        {
            foreach (var field in new[] { "name", "path", "description", "class", "status", "preview", "interactive", "mcpEligible", "options" })
            {
                Assert.NotNull(command![field]);
            }
        }
    }

    [Fact]
    public async Task Commands_table_lists_every_command()
    {
        var (exitCode, output, _) = await RunAsync("commands");

        Assert.Equal(ExitCodes.Success, exitCode);
        foreach (var spec in CommandRegistry.All)
        {
            Assert.Contains(spec.Name, output);
        }
    }
}