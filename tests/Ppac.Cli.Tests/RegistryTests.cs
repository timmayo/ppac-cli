using Ppac.Cli.Registry;

namespace Ppac.Cli.Tests;

public class RegistryTests
{
    private static CommandSpec Spec(CommandClass cls, VerificationStatus status, bool interactive = false) => new()
    {
        Path = new[] { "x" },
        Description = "d",
        Class = cls,
        Status = status,
        Interactive = interactive,
        Handler = (_, _) => Task.FromResult(0)
    };

    [Theory]
    [InlineData(CommandClass.Read, VerificationStatus.Verified, false, true)]
    [InlineData(CommandClass.Write, VerificationStatus.Verified, false, false)]
    [InlineData(CommandClass.Destructive, VerificationStatus.Verified, false, false)]
    [InlineData(CommandClass.Read, VerificationStatus.OperationOnly, false, false)]
    [InlineData(CommandClass.Read, VerificationStatus.Unverified, false, false)]
    [InlineData(CommandClass.Read, VerificationStatus.Verified, true, false)]
    public void McpEligible_requires_verified_read_non_interactive(
        CommandClass cls, VerificationStatus status, bool interactive, bool expected) =>
        Assert.Equal(expected, Spec(cls, status, interactive).McpEligible);

    [Fact]
    public void Command_names_are_unique()
    {
        var names = CommandRegistry.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_command_has_a_description_and_path()
    {
        foreach (var spec in CommandRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), spec.Name);
            Assert.NotEmpty(spec.Path);
        }
    }

    [Fact]
    public void Every_verified_command_documents_its_api()
    {
        foreach (var spec in CommandRegistry.All.Where(s => s.Status == VerificationStatus.Verified))
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Api), $"{spec.Name} has no Api");
        }
    }

    [Fact]
    public void Every_command_accepts_json_output()
    {
        foreach (var spec in CommandRegistry.All)
        {
            Assert.Contains(spec.Options, o => o.Name == "--json");
        }
    }

    [Fact]
    public void Option_names_start_with_dashes_and_are_unique_per_command()
    {
        foreach (var spec in CommandRegistry.All)
        {
            Assert.All(spec.Options, o => Assert.StartsWith("--", o.Name));
            var names = spec.Options.Select(o => o.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    [Fact]
    public void Every_command_group_has_a_description()
    {
        foreach (var spec in CommandRegistry.All.Where(s => s.Path.Length > 1))
        {
            var group = string.Join(' ', spec.Path.Take(spec.Path.Length - 1));
            Assert.True(CommandRegistry.GroupDescriptions.ContainsKey(group), $"No description for group '{group}'");
        }
    }

    [Fact]
    public void Write_and_destructive_commands_are_never_mcp_eligible()
    {
        foreach (var spec in CommandRegistry.All.Where(s => s.Class != CommandClass.Read))
        {
            Assert.False(spec.McpEligible, spec.Name);
        }
    }

    [Fact]
    public void Interactive_commands_are_never_mcp_eligible()
    {
        foreach (var spec in CommandRegistry.All.Where(s => s.Interactive))
        {
            Assert.False(spec.McpEligible, spec.Name);
        }
    }
}