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
    public void Commands_that_change_things_support_whatif_and_destructive_ones_support_yes()
    {
        // auth create is excluded: it is interactive and only writes the local profile store.
        foreach (var spec in CommandRegistry.All.Where(s => s.Class != CommandClass.Read && !s.Interactive))
        {
            Assert.Contains(spec.Options, o => o.Name == "--whatif");

            if (spec.Class == CommandClass.Destructive)
            {
                Assert.Contains(spec.Options, o => o.Name == "--yes");
            }
        }
    }

    [Fact]
    public void Read_commands_and_non_destructive_commands_do_not_offer_yes_or_whatif_by_mistake()
    {
        foreach (var spec in CommandRegistry.All)
        {
            if (spec.Class == CommandClass.Read)
            {
                Assert.DoesNotContain(spec.Options, o => o.Name is "--whatif" or "--yes");
            }
            else if (spec.Class == CommandClass.Write)
            {
                Assert.DoesNotContain(spec.Options, o => o.Name == "--yes");
            }
        }
    }

    [Fact]
    public void Backup_commands_are_registered_with_the_right_class_and_verified_live()
    {
        var backups = CommandRegistry.All.Where(s => s.Path.Take(2).SequenceEqual(new[] { "environment", "backup" })).ToList();

        Assert.Equal(
            new[] { "environment backup create", "environment backup delete", "environment backup list" },
            backups.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(CommandClass.Read, backups.Single(s => s.Path[^1] == "list").Class);
        Assert.Equal(CommandClass.Write, backups.Single(s => s.Path[^1] == "create").Class);
        Assert.Equal(CommandClass.Destructive, backups.Single(s => s.Path[^1] == "delete").Class);
        Assert.All(backups, s => Assert.Equal(VerificationStatus.Verified, s.Status));
        Assert.True(backups.Single(s => s.Path[^1] == "list").McpEligible);
        Assert.False(backups.Single(s => s.Path[^1] == "create").McpEligible);
        Assert.False(backups.Single(s => s.Path[^1] == "delete").McpEligible);
    }

    [Fact]
    public void Group_commands_are_read_only_and_name_their_documented_permission()
    {
        var groups = CommandRegistry.All.Where(s => s.Path.Take(2).SequenceEqual(new[] { "environment", "group" })).ToList();

        Assert.Equal(
            new[] { "environment group get", "environment group list" },
            groups.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(groups, s => Assert.Equal(CommandClass.Read, s.Class));
        Assert.All(groups, s => Assert.Equal("EnvironmentManagement.Groups.Read", s.Permission));
    }

    [Fact]
    public void Read_commands_do_not_require_options_that_have_a_sensible_default()
    {
        // list/get style commands must work with no arguments (top N, or everything).
        var optionalTargets = new[] { "environment list", "environment get", "environment group list", "environment group get" };
        foreach (var name in optionalTargets)
        {
            var spec = CommandRegistry.All.Single(s => s.Name == name);
            Assert.DoesNotContain(spec.Options, o => o.Required);
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