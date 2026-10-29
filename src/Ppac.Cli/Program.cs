using System.CommandLine;
using Ppac.Cli.Commands;

namespace Ppac.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        var root = new RootCommand("ppac - Power Platform admin center CLI");
        root.Subcommands.Add(AuthCommands.Build());
        root.Subcommands.Add(EnvironmentCommands.Build());

        // --help and --version are built into System.CommandLine.
        return root.Parse(args).InvokeAsync();
    }
}