using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

/// <summary>
/// 'ppac environment group list | get'. Source: Environment Management API, environment groups
/// (api-version 2024-10-01). The reference pages list only the .default scope; the permission
/// reference names EnvironmentManagement.Groups.Read. Unverified until run live.
/// </summary>
public static class EnvironmentGroupCommands
{
    private const string ReadPermission = "EnvironmentManagement.Groups.Read";

    // How many groups 'environment group get' lists when --group is omitted.
    private const int DefaultListCount = 10;

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "list" },
            Description = "List the environment groups in the tenant.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Unverified,
            Api = $"GET /environmentmanagement/environmentGroups?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = ReadPermission,
            Options = new[]
            {
                new OptionSpec("--top", Array.Empty<string>(), OptionType.Int,
                    "Maximum number of groups to return. Default: all."),
                JsonOutput.Option
            },
            Handler = ListAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "get" },
            Description = "Show one environment group, or the first 10 groups when --group is omitted.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Unverified,
            Api = $"GET /environmentmanagement/environmentGroups/{{groupId}}?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = ReadPermission,
            Options = new[]
            {
                new OptionSpec("--group", Array.Empty<string>(), OptionType.String,
                    "Group ID, as shown by 'ppac environment group list'. When omitted, the first 10 groups are listed."),
                JsonOutput.Option
            },
            Handler = GetAsync
        };
    }

    private static Task<int> ListAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var top = ctx.GetInt("--top");
        if (top is not null && top < 1)
        {
            Console.Error.WriteLine("--top must be 1 or greater.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var json = ctx.GetBool(JsonOutput.OptionName);
        return RunAsync((api, cloud) => ListCoreAsync(api, cloud, top, json, cancellationToken), cancellationToken);
    }

    private static Task<int> GetAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var groupId = ctx.GetString("--group");
        if (groupId is not null && string.IsNullOrWhiteSpace(groupId))
        {
            Console.Error.WriteLine("--group must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var json = ctx.GetBool(JsonOutput.OptionName);
        return RunAsync(
            (api, cloud) => GetCoreAsync(api, cloud, groupId?.Trim(), json, cancellationToken),
            cancellationToken);
    }

    internal static async Task<int> ListCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, int? top, bool json, CancellationToken cancellationToken)
    {
        var groups = await EnvironmentGroupApi.ListAsync(api, cloud, top, cancellationToken);

        if (json)
        {
            JsonOutput.Write(new JsonObject { ["value"] = groups });
        }
        else
        {
            WriteTable(groups);
        }
        return ExitCodes.Success;
    }

    /// <summary>One group by ID; with no ID, the first DefaultListCount groups (same shape as list).</summary>
    internal static async Task<int> GetCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string? groupId, bool json, CancellationToken cancellationToken)
    {
        if (groupId is null)
        {
            var groups = await EnvironmentGroupApi.ListAsync(api, cloud, DefaultListCount, cancellationToken);

            if (json)
            {
                JsonOutput.Write(new JsonObject { ["value"] = groups });
            }
            else
            {
                WriteTable(groups);
                Console.Error.WriteLine(
                    $"Showing up to {DefaultListCount} groups. Use --group <id> for one group's details, or 'ppac environment group list' for all.");
            }
            return ExitCodes.Success;
        }

        var group = await EnvironmentGroupApi.GetAsync(api, cloud, groupId, cancellationToken);
        if (group is null)
        {
            Console.Error.WriteLine($"The service returned no content for group {groupId}. 'ppac environment group list' shows the group IDs.");
            return ExitCodes.Failure;
        }

        if (json)
        {
            JsonOutput.Write(group);
        }
        else
        {
            WriteDetails(group);
        }
        return ExitCodes.Success;
    }

    private static Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call, CancellationToken cancellationToken) =>
        ApiCommandRunner.RunAsync(
            call,
            forbiddenHint:
                $"Check that the app registration has the delegated permission {ReadPermission} " +
                "with admin consent, that you signed in again after adding it ('ppac auth create'), " +
                "and that the signed-in user has a Power Platform admin role.",
            notFoundHint: "Check the group ID. 'ppac environment group list' shows the IDs.",
            cancellationToken);

    private static void WriteTable(JsonArray groups)
    {
        if (groups.Count == 0)
        {
            Console.WriteLine("No environment groups found.");
            return;
        }

        var rows = groups
            .Select(g => new
            {
                Name = JsonOutput.Str(g, "displayName") ?? "",
                Id = JsonOutput.Str(g, "id") ?? "",
                Parent = JsonOutput.Str(g, "parentGroupId") ?? "",
                Created = JsonOutput.Str(g, "createdTime") ?? "",
                Description = JsonOutput.Str(g, "description") ?? ""
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The parent column is only useful when some group is nested; live data had none.
        var showParent = rows.Any(r => r.Parent.Length > 0);

        Console.WriteLine(
            $"{"Name",-34}{"Group ID",-38}{(showParent ? $"{"Parent group ID",-38}" : "")}{"Created",-34}{"Description"}");
        foreach (var r in rows)
        {
            Console.WriteLine(
                $"{Shorten(r.Name, 32),-34}{r.Id,-38}{(showParent ? $"{r.Parent,-38}" : "")}{r.Created,-34}{Shorten(r.Description, 60)}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} group(s).");
    }

    // Prints the documented properties that are present. --json returns everything.
    private static void WriteDetails(JsonNode group)
    {
        var fields = new (string Label, string? Value)[]
        {
            ("Name", JsonOutput.Str(group, "displayName")),
            ("Group ID", JsonOutput.Str(group, "id")),
            ("Description", JsonOutput.Str(group, "description")),
            ("Parent group ID", JsonOutput.Str(group, "parentGroupId")),
            ("Child group IDs", group["childrenGroupIds"] is JsonArray children && children.Count > 0
                ? string.Join(", ", children.Select(c => c?.ToString()))
                : null),
            ("Created", JsonOutput.Str(group, "createdTime")),
            ("Created by", Principal(group["createdBy"])),
            ("Last modified", JsonOutput.Str(group, "lastModifiedTime")),
            ("Last modified by", Principal(group["lastModifiedBy"]))
        };

        foreach (var (label, value) in fields)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"{label + ":",-18}{value}");
            }
        }
    }

    // Documented Principal properties: displayName, email, id, tenantId, type, userPrincipalName.
    // Show the first one present, since which ones come back is not documented.
    private static string? Principal(JsonNode? principal) =>
        JsonOutput.Str(principal, "displayName")
        ?? JsonOutput.Str(principal, "userPrincipalName")
        ?? JsonOutput.Str(principal, "email")
        ?? JsonOutput.Str(principal, "id");

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
