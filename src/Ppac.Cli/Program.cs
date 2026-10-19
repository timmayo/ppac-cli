using System.CommandLine;
using Ppac.Cli.Commands;
using Ppac.Core;

namespace Ppac.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("ppac - one-stop CLI for Power Platform admins");
        root.Options.Add(GlobalOptions.Output);
        root.Options.Add(GlobalOptions.Verbose);
        root.Options.Add(GlobalOptions.Profile);
        root.Subcommands.Add(AuthCommands.Create());
        root.Subcommands.Add(EnvCommands.Create());
        root.Subcommands.Add(GroupCommands.Create());
        root.Subcommands.Add(OpCommands.Create());
        root.Subcommands.Add(InventoryCommands.Create());
        root.Subcommands.Add(ApiCommand.Create());

        var parse = root.Parse(args);

        // Usage errors exit 2, not the library default, so scripts can tell them from failures.
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors) Console.Error.WriteLine($"error: {error.Message}");
            Console.Error.WriteLine("Run 'ppac --help' for usage.");
            return ExitCodes.Usage;
        }

        return await parse.InvokeAsync();
    }
}