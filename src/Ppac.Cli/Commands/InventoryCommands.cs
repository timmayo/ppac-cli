using System.CommandLine;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Inventory;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

/// <summary>Tenant inventory of apps, flows and agents (the "Maker inventory" page of the admin center).</summary>
public static class InventoryCommands
{
    public static Command Create()
    {
        var inventory = new Command("inventory", "List apps, flows and agents across the tenant");
        inventory.Subcommands.Add(List());
        inventory.Subcommands.Add(Show());
        return inventory;
    }

    private static Command List()
    {
        var type = new Option<string>("--type")
        {
            Description = $"Kind of item: {string.Join(", ", InventoryQuery.TypeNames)} (default: all of them)",
        };
        var env = new Option<string>("--env") { Description = "Only items in this environment (ID or exact display name)" };
        var owner = new Option<string>("--owner") { Description = "Only items owned by this user (Microsoft Entra object ID)" };
        var limit = new Option<int>("--limit")
        {
            Description = "Maximum number of rows (default 100)",
            DefaultValueFactory = _ => 100,
        };
        var all = new Option<bool>("--all") { Description = "Return every row, ignoring --limit" };
        var pageSize = new Option<int>("--page-size")
        {
            Description = $"Rows requested per call, 1 to {InventoryQuery.MaxPageSize} (default {InventoryQuery.MaxPageSize})",
            DefaultValueFactory = _ => InventoryQuery.MaxPageSize,
        };

        var command = new Command("list", "List apps, flows and agents. Needs a signed-in admin (service principals are refused).");
        command.Options.Add(type);
        command.Options.Add(env);
        command.Options.Add(owner);
        command.Options.Add(limit);
        command.Options.Add(all);
        command.Options.Add(pageSize);

        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var types = InventoryQuery.ResolveTypes(parse.GetValue(type));

            var everything = parse.GetValue(all);
            var max = parse.GetValue(limit);
            if (!everything && max < 1)
                throw new PpacException(ExitCodes.Usage, "--limit must be 1 or more. Use --all for every row.");

            var size = parse.GetValue(pageSize);
            if (size < 1 || size > InventoryQuery.MaxPageSize)
                throw new PpacException(ExitCodes.Usage, $"--page-size must be between 1 and {InventoryQuery.MaxPageSize}.");

            var ownerId = parse.GetValue(owner);
            if (!string.IsNullOrWhiteSpace(ownerId) && !Guid.TryParse(ownerId.Trim(), out _))
                throw new PpacException(ExitCodes.Usage, "--owner must be a user's object ID (a GUID).");

            var client = CommandRunner.CreateClient(parse);

            string? environmentId = null;
            if (!string.IsNullOrWhiteSpace(parse.GetValue(env)))
                environmentId = (await WriteHelpers.ResolveEnvironmentAsync(client, parse.GetValue(env)!, token)).Id;

            // The inventory rows carry environment IDs only; the environment list supplies names and types.
            var environments = new Dictionary<string, EnvironmentInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in await client.GetAllAsync($"{WriteHelpers.EnvironmentsPath}?{WriteHelpers.ApiVersion}", token))
            {
                var id = OutputWriter.CellText(e, "id");
                if (id.Length > 0)
                    environments[id] = new EnvironmentInfo(OutputWriter.CellText(e, "displayName"), OutputWriter.CellText(e, "type"));
            }

            var result = await InventoryQuery.QueryAsync(
                client, types, environmentId, ownerId, everything ? null : max, size, token);

            var rows = result.Rows.Select(r => InventoryQuery.Flatten(r, environments)).ToList();
            OutputWriter.Write(Console.Out, rows, Columns.Inventory, format);

            if (result.TotalRecords is { } total && rows.Count < total)
                Console.Error.WriteLine($"Showing {rows.Count} of {total}. Use --all or a larger --limit for more.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Show()
    {
        var item = new Argument<string>("item-id") { Description = "Item ID (the Id column of 'inventory list')" };

        var command = new Command("show", "Show one app, flow or agent. Exits 4 if it is not in the inventory.");
        command.Arguments.Add(item);

        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var itemId = parse.GetValue(item)!.Trim();
            if (!Guid.TryParse(itemId, out _))
                throw new PpacException(ExitCodes.Usage, "item-id must be a GUID (see the Id column of 'inventory list').");

            var client = CommandRunner.CreateClient(parse);
            var (row, usage) = await InventoryQuery.FindAsync(client, itemId, token);
            if (row is null)
                throw new PpacException(ExitCodes.NotFound, $"Item '{itemId}' not found in the inventory.");

            var environments = new Dictionary<string, EnvironmentInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in await client.GetAllAsync($"{WriteHelpers.EnvironmentsPath}?{WriteHelpers.ApiVersion}", token))
            {
                var id = OutputWriter.CellText(e, "id");
                if (id.Length > 0)
                    environments[id] = new EnvironmentInfo(OutputWriter.CellText(e, "displayName"), OutputWriter.CellText(e, "type"));
            }

            ObjectWriter.Write(Console.Out, InventoryQuery.Detail(row, usage, environments), format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}