using Ppac.Cli.Registry;

namespace Ppac.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The command tree is generated from the registry. --help and --version
        // are built into System.CommandLine.
        var root = CommandTreeBuilder.Build(CommandRegistry.All, CommandRegistry.GroupDescriptions);

        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // System.CommandLine returns 1 for an invalid command line; report it as 2
        // so scripts can tell it apart from a command that ran and failed.
        return parseResult.Errors.Count > 0 && exitCode != ExitCodes.Success
            ? ExitCodes.Usage
            : exitCode;
    }
}