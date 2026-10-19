using System.CommandLine;
using Ppac.Core;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

public static class GroupCommands
{
    private const string ListPath = "/environmentmanagement/environmentGroups?api-version=2022-03-01-preview";

    public static Command Create()
    {
        var group = new Command("group", "Manage environment groups");
        group.Subcommands.Add(List());
        group.Subcommands.Add(Show());
        return group;
    }

    private static Command List()
    {
        var command = new Command("list", "List environment groups (use -o json for every field)");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var rows = await CommandRunner.CreateClient(parse).GetAllAsync(ListPath, token);

            OutputWriter.Write(Console.Out, rows, Columns.Groups, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Show()
    {
        var group = new Argument<string>("group") { Description = "Environment group ID, or its exact display name" };

        var command = new Command("show", "Show one environment group. Exits 4 if it does not exist.");
        command.Arguments.Add(group);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var rows = await CommandRunner.CreateClient(parse).GetAllAsync(ListPath, token);
            var found = ResourceLookup.Find(rows, parse.GetValue(group)!, "Environment group");

            ObjectWriter.Write(Console.Out, found, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}