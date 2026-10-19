using System.CommandLine;
using Ppac.Core;
using Ppac.Core.Environments;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

public static class EnvCommands
{
    private const string ListPath = "/environmentmanagement/environments?api-version=2024-10-01";

    public static Command Create()
    {
        var env = new Command("env", "Manage environments");
        env.Subcommands.Add(List());
        env.Subcommands.Add(Show());
        return env;
    }

    private static Command List()
    {
        var command = new Command("list", "List environments (use -o json for every field)");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var rows = await CommandRunner.CreateClient(parse).GetAllAsync(ListPath, token);

            OutputWriter.Write(Console.Out, rows, Columns.Environments, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Show()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };

        var command = new Command("show", "Show one environment. Exits 4 if it does not exist.");
        command.Arguments.Add(environment);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);

            // The list is small and server-side filtering semantics are not verified yet, so match locally.
            var rows = await CommandRunner.CreateClient(parse).GetAllAsync(ListPath, token);
            var found = EnvironmentLookup.Find(rows, parse.GetValue(environment)!);

            ObjectWriter.Write(Console.Out, found, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}