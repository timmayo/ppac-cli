using System.CommandLine;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Http;
using Ppac.Core.Operations;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

/// <summary>Long-running environment operations (copy, delete and so on).</summary>
public static class OpCommands
{
    public static Command Create()
    {
        var op = new Command("op", "Track long-running environment operations");
        op.Subcommands.Add(List());
        op.Subcommands.Add(Show());
        op.Subcommands.Add(Wait());
        return op;
    }

    private static string OperationsPath(string environmentId) =>
        $"{WriteHelpers.EnvPath(environmentId, "/operations")}?{WriteHelpers.ApiVersion}";

    private static string OperationPath(string environmentId, string operationId) =>
        $"{WriteHelpers.EnvPath(environmentId, "/operations/" + Uri.EscapeDataString(operationId))}?{WriteHelpers.ApiVersion}";

    private static Command List()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };

        var command = new Command("list", "List the operations of an environment");
        command.Arguments.Add(environment);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var client = CommandRunner.CreateClient(parse);
            var envId = await WriteHelpers.ResolveEnvironmentIdAsync(client, parse.GetValue(environment)!, token);

            var rows = await client.GetCollectionAsync(OperationsPath(envId), token);
            OutputWriter.Write(Console.Out, rows, Columns.Operations, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Show()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };
        var operationId = new Argument<string>("operation-id") { Description = "Operation ID (see: ppac op list)" };

        var command = new Command("show", "Show one operation. Exits 4 if it does not exist.");
        command.Arguments.Add(environment);
        command.Arguments.Add(operationId);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var client = CommandRunner.CreateClient(parse);
            var envId = await WriteHelpers.ResolveEnvironmentIdAsync(client, parse.GetValue(environment)!, token);

            var response = await client.SendAsync(HttpMethod.Get, OperationPath(envId, parse.GetValue(operationId)!.Trim()), null, token);
            if (response.Json is not JsonObject operation)
                throw new PpacException(ExitCodes.General, "The service returned an empty response for the operation.");

            ObjectWriter.Write(Console.Out, operation, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Wait()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };
        var operationId = new Argument<string>("operation-id") { Description = "Operation ID (see: ppac op list)" };
        var timeout = new Option<string>("--timeout")
        {
            Description = "Give up waiting after this long, for example 30m (the operation keeps running)",
            DefaultValueFactory = _ => "30m",
        };
        var interval = new Option<string>("--interval")
        {
            Description = "How often to check, for example 30s",
            DefaultValueFactory = _ => "30s",
        };

        var command = new Command("wait", "Wait for an operation to finish. Exits 0 on success, 8 if it failed, 9 on timeout.");
        command.Arguments.Add(environment);
        command.Arguments.Add(operationId);
        command.Options.Add(timeout);
        command.Options.Add(interval);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var pollEvery = DurationParser.Parse(parse.GetValue(interval)!, "--interval");
            var giveUpAfter = DurationParser.Parse(parse.GetValue(timeout)!, "--timeout");
            if (pollEvery <= TimeSpan.Zero)
                throw new PpacException(ExitCodes.Usage, "--interval must be greater than zero.");

            var client = CommandRunner.CreateClient(parse);
            var envId = await WriteHelpers.ResolveEnvironmentIdAsync(client, parse.GetValue(environment)!, token);
            var path = OperationPath(envId, parse.GetValue(operationId)!.Trim());

            var final = await OperationWaiter.WaitAsync(
                async t =>
                {
                    var response = await client.SendAsync(HttpMethod.Get, path, null, t);
                    return response.Json as JsonObject
                        ?? throw new PpacException(ExitCodes.General, "The service returned an empty response for the operation.");
                },
                pollEvery, giveUpAfter,
                (wait, t) => Task.Delay(wait, t),
                () => DateTimeOffset.UtcNow,
                message => Console.Error.WriteLine(message),
                token);

            ObjectWriter.Write(Console.Out, final, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}