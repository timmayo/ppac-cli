using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;
using Ppac.Cli.Safety;

namespace Ppac.Cli.Commands;

/// <summary>
/// 'ppac environment backup list | create | delete'.
/// Source: Environment Management API backup operations (api-version 2024-10-01).
/// Verified live 2026-10-30 (create, list, delete on a sandbox). No permission is documented
/// for these calls and none failed, so Permission is left empty: the exact permission that
/// covered them was not identified.
/// </summary>
public static class BackupCommands
{
    private const string ForbiddenHint =
        "No permission is documented for backup operations, so the required one is not known yet. " +
        "Check that the app registration has admin-consented delegated permissions for the Power Platform API " +
        "and that the signed-in user has a Power Platform admin role. Report the exact error so the permission can be recorded.";

    private const string NotFoundHint =
        "Check the environment ID ('ppac environment list') and the backup ID ('ppac environment backup list --environment <id>').";

    private static readonly OptionSpec EnvironmentOption = new(
        "--environment", Array.Empty<string>(), OptionType.String,
        "Environment ID, as shown by 'ppac environment list'.", Required: true);

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "backup", "list" },
            Description = "List the backups of an environment.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Api = $"GET /environmentmanagement/environments/{{environmentId}}/backups?api-version={BackupApi.ApiVersion}",
            Options = new[] { EnvironmentOption, JsonOutput.Option },
            Handler = ListAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "backup", "create" },
            Description = "Create a backup of an environment. Use --whatif to preview the request.",
            Class = CommandClass.Write,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environments/{{environmentId}}/backups?api-version={BackupApi.ApiVersion}",
            Options = new[]
            {
                EnvironmentOption,
                new OptionSpec("--label", Array.Empty<string>(), OptionType.String,
                    "A label for the backup.", Required: true),
                WriteSafety.WhatIfOption,
                JsonOutput.Option
            },
            Handler = CreateAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "backup", "delete" },
            Description = "Delete a backup of an environment. Asks for confirmation unless --yes is given; use --whatif to preview.",
            Class = CommandClass.Destructive,
            Status = VerificationStatus.Verified,
            Api = $"DELETE /environmentmanagement/environments/{{environmentId}}/backups/{{backupId}}?api-version={BackupApi.ApiVersion}",
            Options = new[]
            {
                EnvironmentOption,
                new OptionSpec("--backup", Array.Empty<string>(), OptionType.String,
                    "Backup ID, as shown by 'ppac environment backup list'.", Required: true),
                WriteSafety.WhatIfOption,
                WriteSafety.YesOption,
                JsonOutput.Option
            },
            Handler = DeleteAsync
        };
    }

    // ---- list ----

    private static Task<int> ListAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var environmentId = ctx.GetString("--environment");
        if (!IsPresent(environmentId, "--environment"))
        {
            return Task.FromResult(ExitCodes.Usage);
        }

        var json = ctx.GetBool(JsonOutput.OptionName);
        return RunAsync(
            (api, cloud) => ListCoreAsync(api, cloud, environmentId!.Trim(), json, cancellationToken),
            json,
            cancellationToken);
    }

    internal static async Task<int> ListCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, bool json, CancellationToken cancellationToken)
    {
        var page = await BackupApi.ListAsync(api, cloud, environmentId, cancellationToken);

        if (json)
        {
            var result = new JsonObject { ["collection"] = page.Items };
            if (page.ContinuationToken is not null)
            {
                result["continuationToken"] = page.ContinuationToken;
            }
            JsonOutput.Write(result);
        }
        else
        {
            WriteTable(page.Items);
        }

        if (page.ContinuationToken is not null)
        {
            Console.Error.WriteLine(
                "The service returned a continuation token, so more backups may exist. " +
                "Paging is not documented, so only the first page is shown.");
        }
        return ExitCodes.Success;
    }

    // ---- create ----

    private static Task<int> CreateAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var environmentId = ctx.GetString("--environment");
        var label = ctx.GetString("--label");
        if (!IsPresent(environmentId, "--environment") || !IsPresent(label, "--label"))
        {
            return Task.FromResult(ExitCodes.Usage);
        }

        environmentId = environmentId!.Trim();
        var json = ctx.GetBool(JsonOutput.OptionName);

        if (ctx.GetBool(WriteSafety.WhatIfName))
        {
            // --whatif for create is local: no sign-in and no request. The cloud comes from the
            // active profile when there is one.
            var profile = ProfileStore.GetActive();
            var cloud = profile is null ? CloudConfig.Commercial : CloudConfig.Get(profile.Cloud);

            WriteSafety.WriteWhatIf(
                "environment backup create",
                $"create a backup labeled '{label}' of environment {environmentId}.",
                BackupApi.CreateRequest(cloud, environmentId, label!),
                json);
            return Task.FromResult(ExitCodes.Success);
        }

        return RunAsync(
            (api, cloud) => CreateCoreAsync(api, cloud, environmentId, label!, json, cancellationToken),
            json,
            cancellationToken);
    }

    internal static async Task<int> CreateCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, string label, bool json,
        CancellationToken cancellationToken)
    {
        var backup = await api.SendAsync(BackupApi.CreateRequest(cloud, environmentId, label), cancellationToken);

        if (json)
        {
            JsonOutput.Write(backup ?? new JsonObject { ["created"] = true });
        }
        else
        {
            Console.WriteLine("Backup created.");
            if (backup is not null)
            {
                WriteDetails(backup);
            }
        }
        return ExitCodes.Success;
    }

    // ---- delete ----

    private static Task<int> DeleteAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var environmentId = ctx.GetString("--environment");
        var backupId = ctx.GetString("--backup");
        if (!IsPresent(environmentId, "--environment") || !IsPresent(backupId, "--backup"))
        {
            return Task.FromResult(ExitCodes.Usage);
        }

        var whatIf = ctx.GetBool(WriteSafety.WhatIfName);
        var yes = ctx.GetBool(WriteSafety.YesName);
        var json = ctx.GetBool(JsonOutput.OptionName);
        var interactive = WriteSafety.IsInteractive();

        // Fail fast, before signing in, when a prompt is needed but nobody can answer it.
        if (!whatIf && !yes && !interactive)
        {
            WriteSafety.WriteRefusal("Deleting a backup");
            return Task.FromResult(ExitCodes.Usage);
        }

        return RunAsync(
            (api, cloud) => DeleteCoreAsync(
                api, cloud, environmentId!.Trim(), backupId!.Trim(),
                whatIf, yes, json, interactive, Console.In, cancellationToken),
            json,
            cancellationToken);
    }

    internal static async Task<int> DeleteCoreAsync(
        PowerPlatformApi api,
        CloudConfig cloud,
        string environmentId,
        string backupId,
        bool whatIf,
        bool yes,
        bool json,
        bool interactive,
        TextReader input,
        CancellationToken cancellationToken)
    {
        // Look the backup up first so --whatif and the prompt can say exactly what would be removed.
        var lookup = await BackupApi.FindAsync(api, cloud, environmentId, backupId, cancellationToken);
        var (environmentName, environmentType) = await TryGetEnvironmentAsync(api, cloud, environmentId, cancellationToken);

        var environmentText = DescribeEnvironment(environmentId, environmentName, environmentType);
        var label = JsonOutput.Str(lookup.Backup, "label");
        var backupText = label is null ? $"backup {backupId}" : $"backup '{label}' ({backupId})";
        var request = BackupApi.DeleteRequest(cloud, environmentId, backupId);

        if (lookup.NotFound)
        {
            var message = $"Backup {backupId} was not found in {environmentText}. " +
                          "'ppac environment backup list --environment <id>' shows the backups.";
            return ErrorReport.Write(
                ExitCodes.NotFound, message, json,
                details: new JsonObject { ["lookup"] = "notFound", ["whatIf"] = whatIf });
        }

        var summary = $"delete {backupText} from {environmentText}.";

        if (whatIf)
        {
            var lines = new List<string>();
            if (lookup.Found)
            {
                lines.Add($"Backup point: {JsonOutput.Str(lookup.Backup, "backupPointDateTime") ?? "-"}");
                lines.Add($"Expires:      {JsonOutput.Str(lookup.Backup, "backupExpiryDateTime") ?? "-"}");
                lines.Add($"Created by:   {CreatedBy(lookup.Backup) ?? "-"}");
            }
            else
            {
                lines.Add("The backup was not on the first page of the list, and paging is not documented, so it could not be checked.");
            }

            var details = new JsonObject
            {
                ["lookup"] = lookup.Found ? "found" : "unknown",
                ["environmentName"] = environmentName,
                ["backup"] = lookup.Backup?.DeepClone()
            };

            WriteSafety.WriteWhatIf("environment backup delete", summary, request, json, lines, details);
            return ExitCodes.Success;
        }

        switch (WriteSafety.Confirm(char.ToUpperInvariant(summary[0]) + summary[1..], yes, interactive, input, Console.Error))
        {
            case ConfirmResult.Refused:
                WriteSafety.WriteRefusal("Deleting a backup");
                return ExitCodes.Usage;
            case ConfirmResult.Declined:
                return ErrorReport.Write(ExitCodes.Declined, "Cancelled. Nothing was deleted.", json);
        }

        await api.SendAsync(request, cancellationToken);

        if (json)
        {
            JsonOutput.Write(new JsonObject
            {
                ["deleted"] = true,
                ["environmentId"] = environmentId,
                ["backupId"] = backupId
            });
        }
        else
        {
            Console.WriteLine($"Deleted {backupText} from {environmentText}.");
        }
        return ExitCodes.Success;
    }

    // ---- helpers ----

    private static Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call, bool json, CancellationToken cancellationToken) =>
        ApiCommandRunner.RunAsync(call, ForbiddenHint, NotFoundHint, cancellationToken, json);

    private static bool IsPresent(string? value, string optionName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        Console.Error.WriteLine($"{optionName} must not be empty.");
        return false;
    }

    // Best effort: the name only makes the preview and prompt clearer. A failure here must
    // not block or change the operation.
    private static async Task<(string? Name, string? Type)> TryGetEnvironmentAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, CancellationToken cancellationToken)
    {
        try
        {
            var environment = await EnvironmentApi.GetAsync(api, cloud, environmentId, "displayName,type", cancellationToken);
            return (JsonOutput.Str(environment, "displayName"), JsonOutput.Str(environment, "type"));
        }
        catch (ApiException)
        {
            return (null, null);
        }
    }

    private static string DescribeEnvironment(string environmentId, string? name, string? type)
    {
        if (name is null)
        {
            return $"environment {environmentId}";
        }
        var kind = type is null ? "" : $"{type}, ";
        return $"environment '{name}' ({kind}{environmentId})";
    }

    private static void WriteTable(JsonArray backups)
    {
        if (backups.Count == 0)
        {
            Console.WriteLine("No backups found.");
            return;
        }

        var rows = backups
            .Select(b => new
            {
                Label = JsonOutput.Str(b, "label") ?? "",
                Point = JsonOutput.Str(b, "backupPointDateTime") ?? "",
                Expires = JsonOutput.Str(b, "backupExpiryDateTime") ?? "",
                By = CreatedBy(b) ?? "",
                Id = JsonOutput.Str(b, "id") ?? ""
            })
            .OrderByDescending(r => r.Point, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"{"Label",-30}{"Backup point",-34}{"Expires",-34}{"Created by",-38}{"Backup ID"}");
        foreach (var r in rows)
        {
            Console.WriteLine($"{Shorten(r.Label, 28),-30}{r.Point,-34}{r.Expires,-34}{r.By,-38}{r.Id}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} backup(s).");
    }

    private static void WriteDetails(JsonNode backup)
    {
        var fields = new (string Label, string? Value)[]
        {
            ("Label", JsonOutput.Str(backup, "label")),
            ("Backup ID", JsonOutput.Str(backup, "id")),
            ("Backup point", JsonOutput.Str(backup, "backupPointDateTime")),
            ("Expires", JsonOutput.Str(backup, "backupExpiryDateTime")),
            ("Created by", CreatedBy(backup))
        };

        foreach (var (label, value) in fields)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"{label + ":",-14}{value}");
            }
        }
    }

    // The live response carries only createdBy.userId; displayName is documented but was not returned.
    private static string? CreatedBy(JsonNode? backup) =>
        JsonOutput.Str(backup?["createdBy"], "displayName") ?? JsonOutput.Str(backup?["createdBy"], "userId");

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
