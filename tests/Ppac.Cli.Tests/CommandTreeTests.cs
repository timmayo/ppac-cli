using Ppac.Cli.Registry;

namespace Ppac.Cli.Tests;

public class CommandTreeTests
{
    private static System.CommandLine.RootCommand Root() =>
        CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);

    [Fact]
    public void Tree_has_a_subcommand_for_every_registry_path()
    {
        var root = Root();
        foreach (var spec in CommandRegistry.All)
        {
            System.CommandLine.Command current = root;
            foreach (var part in spec.Path)
            {
                var next = current.Subcommands.FirstOrDefault(c => c.Name == part);
                Assert.True(next is not null, $"Missing '{part}' in '{spec.Name}'");
                current = next!;
            }
        }
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("environment nonsense")]
    [InlineData("environment get --select")]              // --select needs a value
    [InlineData("auth create --deviceCode")]              // --tenant and --applicationId are required
    [InlineData("environment list --top notanumber")]
    [InlineData("environment backup")]                    // a group, not a command
    [InlineData("environment backup list")]               // --environment is required
    [InlineData("environment backup create --environment e")]            // --label is required
    [InlineData("environment backup create --label x")]                  // --environment is required
    [InlineData("environment backup delete --environment e")]            // --backup is required
    [InlineData("environment backup delete --backup b")]                 // --environment is required
    [InlineData("environment backup list --environment e --yes")]        // --yes is only for destructive commands
    [InlineData("environment backup create --environment e --label x --yes")]
    [InlineData("environment list --whatif")]             // --whatif is only for commands that change things
    public void Invalid_command_lines_produce_parse_errors(string line)
    {
        var result = Root().Parse(line.Split(' '));
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("commands")]
    [InlineData("commands --json")]
    [InlineData("auth list")]
    [InlineData("auth who --json")]
    [InlineData("environment list --top 3 --filter x --json")]
    [InlineData("environment get")]                       // --environment is optional: lists the first 10
    [InlineData("environment get --json")]
    [InlineData("environment get --environment abc --select displayName --json")]
    [InlineData("auth create --deviceCode --tenant t --applicationId a")]
    [InlineData("environment backup list --environment e")]
    [InlineData("environment backup list --environment e --json")]
    [InlineData("environment backup create --environment e --label x")]
    [InlineData("environment backup create --environment e --label x --whatif --json")]
    [InlineData("environment backup delete --environment e --backup b")]
    [InlineData("environment backup delete --environment e --backup b --whatif")]
    [InlineData("environment backup delete --environment e --backup b --yes --json")]
    public void Valid_command_lines_parse_without_errors(string line)
    {
        var result = Root().Parse(line.Split(' '));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Short_aliases_parse()
    {
        var result = Root().Parse("auth create -dc -t tenant -id app -n name".Split(' '));
        Assert.Empty(result.Errors);
    }
}