using System.CommandLine;
using System.Text.Json.Nodes;
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
        env.Subcommands.Add(Delete());
        env.Subcommands.Add(Copy());
        env.Subcommands.Add(Backup());
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

    private static Command Delete()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };
        var yes = WriteHelpers.YesOption();
        var whatIf = WriteHelpers.WhatIfOption();
        var wait = WriteHelpers.WaitOption();
        var timeout = WriteHelpers.TimeoutOption();
        var interval = WriteHelpers.IntervalOption();

        var command = new Command("delete", "Delete an environment. Asks you to type its name; scripts must pass --yes.");
        command.Arguments.Add(environment);
        command.Options.Add(yes);
        command.Options.Add(whatIf);
        command.Options.Add(wait);
        command.Options.Add(timeout);
        command.Options.Add(interval);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var waitSettings = WriteHelpers.ParseWait(parse, wait, timeout, interval);
            var client = CommandRunner.CreateClient(parse);
            var env = await WriteHelpers.ResolveEnvironmentAsync(client, parse.GetValue(environment)!, token);
            var path = WriteHelpers.EnvPath(env.Id);

            if (parse.GetValue(whatIf))
            {
                var check = await client.SendAsync(HttpMethod.Delete,
                    $"{path}?ValidateOnly=true&{WriteHelpers.ApiVersion}", null, token);
                WriteHelpers.ReportValidation(check, $"delete environment '{env.Name}' ({env.Id})", $"ppac op list {env.Id}");
                return ExitCodes.Ok;
            }

            WriteHelpers.CreateConfirmation().Require($"delete environment '{env.Name}' ({env.Id})", env.Name, parse.GetValue(yes));

            var response = await client.SendAsync(HttpMethod.Delete, $"{path}?{WriteHelpers.ApiVersion}", null, token);
            var operationId = WriteHelpers.WriteAccepted(parse, "delete", env, response, format, quiet: waitSettings is not null);
            if (waitSettings is not null)
                await WriteHelpers.WaitAfterAcceptedAsync(client, env, operationId, waitSettings, format, token);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Copy()
    {
        var from = new Option<string>("--from") { Description = "Source environment (ID or exact display name)" };
        var to = new Option<string>("--to") { Description = "Target environment (ID or exact display name). It is overwritten." };
        var type = new Option<string>("--type") { Description = "minimal or full (required)" };
        var skipAudit = new Option<bool>("--skip-audit-data") { Description = "Do not copy audit data" };
        var nameOverride = new Option<string>("--name-override") { Description = "Environment name to set on the target" };
        var securityGroup = new Option<string>("--security-group-override") { Description = "Security group ID to set on the target" };
        var yes = WriteHelpers.YesOption();
        var whatIf = WriteHelpers.WhatIfOption();
        var wait = WriteHelpers.WaitOption();
        var timeout = WriteHelpers.TimeoutOption();
        var interval = WriteHelpers.IntervalOption();

        var command = new Command("copy", "Copy one environment over another (the target is overwritten).");
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(type);
        command.Options.Add(skipAudit);
        command.Options.Add(nameOverride);
        command.Options.Add(securityGroup);
        command.Options.Add(yes);
        command.Options.Add(whatIf);
        command.Options.Add(wait);
        command.Options.Add(timeout);
        command.Options.Add(interval);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var waitSettings = WriteHelpers.ParseWait(parse, wait, timeout, interval);
            var fromText = parse.GetValue(from);
            var toText = parse.GetValue(to);
            var typeText = parse.GetValue(type);
            if (string.IsNullOrWhiteSpace(fromText) || string.IsNullOrWhiteSpace(toText) || string.IsNullOrWhiteSpace(typeText))
                throw new PpacException(ExitCodes.Usage, "ppac env copy requires --from, --to and --type (minimal or full).");

            var copyType = typeText.Trim().ToLowerInvariant() switch
            {
                "minimal" => "Minimal",
                "full" => "Full",
                _ => throw new PpacException(ExitCodes.Usage, $"Unknown --type '{typeText}'. Use minimal or full."),
            };

            var client = CommandRunner.CreateClient(parse);
            var source = await WriteHelpers.ResolveEnvironmentAsync(client, fromText, token);
            var target = await WriteHelpers.ResolveEnvironmentAsync(client, toText, token);
            if (string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                throw new PpacException(ExitCodes.Usage, "The source and target are the same environment.");

            var body = new JsonObject { ["copyType"] = copyType, ["sourceEnvironmentId"] = source.Id };
            var options = new JsonObject();
            if (parse.GetValue(skipAudit)) options["skipAuditData"] = true;
            if (!string.IsNullOrWhiteSpace(parse.GetValue(nameOverride))) options["environmentNameToOverride"] = parse.GetValue(nameOverride);
            if (!string.IsNullOrWhiteSpace(parse.GetValue(securityGroup))) options["securityGroupIdToOverride"] = parse.GetValue(securityGroup);
            if (options.Count > 0) body["copyOptions"] = options;

            var path = WriteHelpers.EnvPath(target.Id, "/copy");
            var description = $"copy '{source.Name}' ({source.Id}) over '{target.Name}' ({target.Id}) as {copyType}";

            if (parse.GetValue(whatIf))
            {
                // The service does not honor ValidateOnly on copy: a live validation-only request returned 202 and
                // started a real Copy operation. So --whatif for copy sends nothing and only reports what it resolved.
                Console.Error.WriteLine($"Nothing was sent. Would {description}.");
                Console.Error.WriteLine("The service cannot validate a copy without running it, so the request itself was not checked.");
                return ExitCodes.Ok;
            }

            WriteHelpers.CreateConfirmation().Require(
                $"{description}. The target environment will be overwritten", target.Name, parse.GetValue(yes));

            var response = await client.SendAsync(HttpMethod.Post, $"{path}?{WriteHelpers.ApiVersion}", body.ToJsonString(), token);
            var operationId = WriteHelpers.WriteAccepted(parse, "copy", target, response, format, quiet: waitSettings is not null);
            if (waitSettings is not null)
                await WriteHelpers.WaitAfterAcceptedAsync(client, target, operationId, waitSettings, format, token);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Backup()
    {
        var backup = new Command("backup", "Manage environment backups");
        backup.Subcommands.Add(BackupCreate());
        backup.Subcommands.Add(BackupList());
        backup.Subcommands.Add(BackupDelete());
        return backup;
    }

    private static Command BackupCreate()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };
        var label = new Option<string>("--label") { Description = "Label for the backup (required)" };
        var whatIf = WriteHelpers.WhatIfOption();

        var command = new Command("create", "Create a manual backup of an environment");
        command.Arguments.Add(environment);
        command.Options.Add(label);
        command.Options.Add(whatIf);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var labelText = parse.GetValue(label);
            if (string.IsNullOrWhiteSpace(labelText))
                throw new PpacException(ExitCodes.Usage, "ppac env backup create requires --label.");

            var client = CommandRunner.CreateClient(parse);
            var env = await WriteHelpers.ResolveEnvironmentAsync(client, parse.GetValue(environment)!, token);

            if (parse.GetValue(whatIf))
            {
                // The service documents no validation-only mode for backups, so this is a local dry run.
                Console.Error.WriteLine($"Nothing was sent. Would create a backup of '{env.Name}' ({env.Id}) labelled '{labelText}'.");
                return ExitCodes.Ok;
            }

            var body = new JsonObject { ["label"] = labelText }.ToJsonString();
            var response = await client.SendAsync(HttpMethod.Post,
                $"{WriteHelpers.EnvPath(env.Id, "/backups")}?{WriteHelpers.ApiVersion}", body, token);

            if (response.Json is JsonObject created) ObjectWriter.Write(Console.Out, created, format);
            else Console.Error.WriteLine($"Backup requested (HTTP {(int)response.Status}).");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command BackupList()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };

        var command = new Command("list", "List the backups of an environment");
        command.Arguments.Add(environment);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var client = CommandRunner.CreateClient(parse);
            var env = await WriteHelpers.ResolveEnvironmentAsync(client, parse.GetValue(environment)!, token);

            var rows = await client.GetCollectionAsync($"{WriteHelpers.EnvPath(env.Id, "/backups")}?{WriteHelpers.ApiVersion}", token);
            OutputWriter.Write(Console.Out, rows, Columns.Backups, format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command BackupDelete()
    {
        var environment = new Argument<string>("environment") { Description = "Environment ID, or its exact display name" };
        var backupId = new Argument<string>("backup-id") { Description = "Backup ID (see: ppac env backup list)" };
        var yes = WriteHelpers.YesOption();
        var whatIf = WriteHelpers.WhatIfOption();

        var command = new Command("delete", "Delete a backup. Asks you to type its label; scripts must pass --yes.");
        command.Arguments.Add(environment);
        command.Arguments.Add(backupId);
        command.Options.Add(yes);
        command.Options.Add(whatIf);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var client = CommandRunner.CreateClient(parse);
            var env = await WriteHelpers.ResolveEnvironmentAsync(client, parse.GetValue(environment)!, token);

            var id = parse.GetValue(backupId)!.Trim();
            var backups = await client.GetCollectionAsync($"{WriteHelpers.EnvPath(env.Id, "/backups")}?{WriteHelpers.ApiVersion}", token);
            var found = backups.FirstOrDefault(b => string.Equals(OutputWriter.CellText(b, "id"), id, StringComparison.OrdinalIgnoreCase))
                ?? throw new PpacException(ExitCodes.NotFound, $"Backup '{id}' not found in environment '{env.Name}'.");

            var realId = OutputWriter.CellText(found, "id");
            var label = OutputWriter.CellText(found, "label");
            var typedName = label.Length > 0 ? label : realId;
            var description = $"delete backup '{typedName}' ({realId}) of environment '{env.Name}'";

            if (parse.GetValue(whatIf))
            {
                Console.Error.WriteLine($"Nothing was sent. Would {description}.");
                return ExitCodes.Ok;
            }

            WriteHelpers.CreateConfirmation().Require(description, typedName, parse.GetValue(yes));

            await client.SendAsync(HttpMethod.Delete,
                $"{WriteHelpers.EnvPath(env.Id, "/backups/" + Uri.EscapeDataString(realId))}?{WriteHelpers.ApiVersion}", null, token);
            Console.Error.WriteLine($"Deleted backup {realId}.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}