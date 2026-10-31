using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

/// <summary>
/// 'ppac environment operation get' and 'ppac environment group operation get'. Source: Environment Management API,
/// operation status (api-version 2024-10-01; see OperationApi for the documented routes and response shapes).
/// Changes such as Managed Environments on/off and adding or removing an environment from a group return 202 and an
/// Operation-Location header; these commands read that operation. Reading an operation succeeds (exit 0) whatever the
/// operation's own status is, so a script reads the Status field: a Failed operation is not a failed command.
/// Not yet run live, so both specs are Unverified; flip to Verified after a live run.
/// </summary>
public static class OperationCommands
{
    private const string OperationHelp =
        "Operation ID, or the whole Operation-Location URL a change printed (for example after 'ppac environment managed enable').";

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "operation", "get" },
            Description = "Show the status of an environment operation, such as turning Managed Environments on or off. Pass the Operation-Location URL a change printed, or an operation ID with --environment.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Unverified,
            Api = $"GET /environmentmanagement/environments/{{targetEnvironmentId}}/operations/{{operationId}}?api-version={OperationApi.ApiVersion}",
            Options = new[]
            {
                new OptionSpec("--operation", Array.Empty<string>(), OptionType.String, OperationHelp, Required: true),
                new OptionSpec("--environment", Array.Empty<string>(), OptionType.String,
                    "Environment ID. Not needed when --operation is a full Operation-Location URL."),
                JsonOutput.Option
            },
            Handler = GetEnvironmentOperationAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "operation", "get" },
            Description = "Show the status of an environment group operation, such as adding or removing an environment. Pass the Operation-Location URL a change printed, or an operation ID.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Unverified,
            Api = $"GET /environmentmanagement/environmentGroupOperations/{{operationId}}?api-version={OperationApi.ApiVersion}",
            Options = new[]
            {
                new OptionSpec("--operation", Array.Empty<string>(), OptionType.String,
                    "Operation ID, or the whole Operation-Location URL a change printed (for example after 'ppac environment group add-environment').", Required: true),
                JsonOutput.Option
            },
            Handler = GetGroupOperationAsync
        };
    }

    private static Task<int> GetEnvironmentOperationAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var json = ctx.GetBool(JsonOutput.OptionName);
        var operation = ctx.GetString("--operation") ?? "";
        var environment = ctx.GetString("--environment");

        if (!OperationApi.TryParseEnvironmentOperation(operation, out var urlEnvironment, out var operationId))
        {
            Console.Error.WriteLine(
                "--operation must be an operation ID or an Operation-Location URL like .../environments/<environment id>/operations/<operation id>.");
            return Task.FromResult(ExitCodes.Usage);
        }

        if (environment is not null && string.IsNullOrWhiteSpace(environment))
        {
            Console.Error.WriteLine("--environment must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var environmentId = environment?.Trim() ?? urlEnvironment;
        if (environmentId is null)
        {
            Console.Error.WriteLine("--environment is required unless --operation is a full Operation-Location URL.");
            return Task.FromResult(ExitCodes.Usage);
        }
        if (urlEnvironment is not null && !string.Equals(urlEnvironment, environmentId, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--environment does not match the environment in the --operation URL.");
            return Task.FromResult(ExitCodes.Usage);
        }

        return ApiCommandRunner.RunAsync(
            (api, cloud) => GetEnvironmentOperationCoreAsync(api, cloud, environmentId, operationId, json, cancellationToken),
            ForbiddenHint, NotFoundHint, cancellationToken, json);
    }

    private static Task<int> GetGroupOperationAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var json = ctx.GetBool(JsonOutput.OptionName);
        if (!OperationApi.TryParseGroupOperation(ctx.GetString("--operation") ?? "", out var operationId))
        {
            Console.Error.WriteLine(
                "--operation must be an operation ID or an Operation-Location URL like .../environmentGroupOperations/<operation id>.");
            return Task.FromResult(ExitCodes.Usage);
        }

        return ApiCommandRunner.RunAsync(
            (api, cloud) => GetGroupOperationCoreAsync(api, cloud, operationId, json, cancellationToken),
            ForbiddenHint, NotFoundHint, cancellationToken, json);
    }

    private const string ForbiddenHint =
        "The reference pages list only the .default scope and name no permission, so which delegated permission this needs is not documented. " +
        "Check that you signed in again after changing the app registration ('ppac auth create') and that you have a Power Platform admin role.";

    private const string NotFoundHint =
        "Check the operation ID (and environment ID). The Operation-Location URL printed by the change that started it has both.";

    internal static async Task<int> GetEnvironmentOperationCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, string operationId, bool json, CancellationToken cancellationToken)
    {
        var operation = await api.SendAsync(OperationApi.EnvironmentOperationRequest(cloud, environmentId, operationId), cancellationToken);
        if (operation is null)
        {
            return NoContent(operationId, json);
        }

        if (json)
        {
            JsonOutput.Write(operation);
        }
        else
        {
            WriteEnvironmentOperation(operation);
        }
        return ExitCodes.Success;
    }

    internal static async Task<int> GetGroupOperationCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string operationId, bool json, CancellationToken cancellationToken)
    {
        var operation = await api.SendAsync(OperationApi.GroupOperationRequest(cloud, operationId), cancellationToken);
        if (operation is null)
        {
            return NoContent(operationId, json);
        }

        if (json)
        {
            JsonOutput.Write(operation);
        }
        else
        {
            WriteGroupOperation(operation);
        }
        return ExitCodes.Success;
    }

    private static int NoContent(string operationId, bool json) =>
        ErrorReport.Write(
            ExitCodes.NotFound,
            $"The service returned no content for operation {operationId}. Check the operation ID.",
            json,
            status: 204,
            title: "No Content");

    // ---- text output ----

    private static void WriteEnvironmentOperation(JsonNode operation)
    {
        var environment = operation["updatedEnvironment"];
        var environmentName = JsonOutput.Str(environment, "displayName");
        var environmentId = JsonOutput.Str(environment, "environmentId");

        Print("Operation", JsonOutput.Str(operation, "name"));
        Print("Operation ID", JsonOutput.Str(operation, "operationId"));
        Print("Status", JsonOutput.Str(operation, "status"));
        Print("Started", JsonOutput.Str(operation, "startTime"));
        Print("Ended", JsonOutput.Str(operation, "endTime"));
        Print("Requested by", JsonOutput.Str(operation["requestedBy"], "displayName") ?? JsonOutput.Str(operation["requestedBy"], "userId"));
        Print("Environment", environmentName is null ? environmentId : $"{environmentName} ({environmentId})");

        if (operation["stageStatuses"] is JsonArray stages && stages.Count > 0)
        {
            Console.WriteLine("Stages:");
            foreach (var stage in stages)
            {
                Console.WriteLine($"  {JsonOutput.Str(stage, "name") ?? "-",-34}{JsonOutput.Str(stage, "status") ?? "-"}");
                var stageError = ErrorText(stage?["errorDetail"]);
                if (stageError is not null)
                {
                    Console.WriteLine($"    Error: {stageError}");
                }
            }
        }

        var error = ErrorText(operation["errorDetail"]);
        if (error is not null)
        {
            Console.WriteLine($"Error:           {error}");
        }
    }

    // The reference defines no response body for this operation, so print the common fields when present and
    // fall back to the JSON itself. --json always returns the whole body.
    private static void WriteGroupOperation(JsonNode operation)
    {
        // Live (2026-10-30) the service answered with a lifecycle-operation shape, not the OperationExecutionResult of the
        // environment route: id, typeDisplayName, state.id, createdDateTime, lastActionDateTime, requestedBy.id,
        // links.environment.path and stages[] with name and state.id. Show that shape when it is there.
        if (operation["state"] is JsonObject && JsonOutput.Str(operation["state"], "id") is { } stateId)
        {
            Print("Operation", JsonOutput.Str(operation, "typeDisplayName"));
            Print("Operation ID", JsonOutput.Str(operation, "id"));
            Print("Status", stateId);
            Print("Created", JsonOutput.Str(operation, "createdDateTime"));
            Print("Last action", JsonOutput.Str(operation, "lastActionDateTime"));
            Print("Requested by", JsonOutput.Str(operation["requestedBy"], "id"));
            var environmentPath = JsonOutput.Str(operation["links"]?["environment"], "path");
            Print("Environment", environmentPath?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
            if (operation["stages"] is JsonArray stages && stages.Count > 0)
            {
                Console.WriteLine("Stages:");
                foreach (var stage in stages)
                {
                    Console.WriteLine($"  {JsonOutput.Str(stage, "name") ?? "-",-34}{JsonOutput.Str(stage?["state"], "id") ?? "-"}");
                }
            }
            return;
        }

        var known = new[] { "name", "operationId", "status", "startTime", "endTime" };
        if (known.Any(k => JsonOutput.Str(operation, k) is not null))
        {
            Print("Operation", JsonOutput.Str(operation, "name"));
            Print("Operation ID", JsonOutput.Str(operation, "operationId"));
            Print("Status", JsonOutput.Str(operation, "status"));
            Print("Started", JsonOutput.Str(operation, "startTime"));
            Print("Ended", JsonOutput.Str(operation, "endTime"));
            var error = ErrorText(operation["errorDetail"]);
            if (error is not null)
            {
                Console.WriteLine($"Error:           {error}");
            }
            Console.Error.WriteLine("The reference does not define this response; use --json for everything the service returned.");
            return;
        }

        JsonOutput.Write(operation);
    }

    private static void Print(string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Console.WriteLine($"{label + ":",-17}{value}");
        }
    }

    // OperationErrorDetail: code and fieldErrors (field -> errorMessages[], suggestedValue).
    private static string? ErrorText(JsonNode? detail)
    {
        if (detail is not JsonObject)
        {
            return null;
        }

        var parts = new List<string>();
        var code = JsonOutput.Str(detail, "code");
        if (!string.IsNullOrWhiteSpace(code))
        {
            parts.Add(code);
        }
        if (detail["fieldErrors"] is JsonObject fields)
        {
            foreach (var (field, error) in fields)
            {
                var messages = error?["errorMessages"] is JsonArray list
                    ? string.Join(" ", list.Select(m => m?.ToString()))
                    : "";
                parts.Add($"{field}: {messages}".Trim());
            }
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
