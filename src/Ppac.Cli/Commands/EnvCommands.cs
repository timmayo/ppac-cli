using System.CommandLine;
using Ppac.Core;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

public static class EnvCommands
{
    public static Command Create()
    {
        var env = new Command("env", "Manage environments");
        env.Subcommands.Add(List());
        return env;
    }

    private static Command List()
    {
        var command = new Command("list", "List environments (use -o json for every field)");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var rows = await CommandRunner.CreateClient(parse)
                .GetAllAsync("/environmentmanagement/environments?api-version=2024-10-01", token);

            OutputWriter.Write(Console.Out, rows, Columns.Environments, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}