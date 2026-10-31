using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;
using Ppac.Cli.Safety;

namespace Ppac.Cli.Commands;

/// <summary>
/// 'ppac environment group create | delete'. Source: Environment Management API, environment groups
/// (api-version 2024-10-01). The reference pages list only the .default scope; the permission reference
/// names EnvironmentManagement.Groups.ReadWrite ("Read and write environment groups"). Verified live
/// 2026-10-30: create returned the new group (200 with a body), delete succeeded, and the group left the list.
/// Add and remove environment verified live 2026-10-30: 202 Accepted with an Operation-Location header
/// (.../environmentGroupOperations/{id}?api-version=1, undocumented), membership visible within seconds.
/// A group that still holds environments cannot be deleted: the service answers 400 "Request failed with status
/// code Conflict for correlationId ..." and changes nothing (verified live), so delete checks membership first
/// and explains how to remove the environments.
/// </summary>
public static class EnvironmentGroupWriteCommands
{
    private const string WritePermission = "EnvironmentManagement.Groups.ReadWrite";

    private const string ForbiddenHint =
        $"Check that the app registration has the delegated permission {WritePermission} " +
        "with admin consent, that you signed in again after adding it ('ppac auth create'), " +
        "and that the signed-in user is a Power Platform tenant administrator (the admin center " +
        "documentation says only tenant administrators can create groups).";

    private const string NotFoundHint =
        "Check the group ID ('ppac environment group list') and, for add-environment and remove-environment, the environment ID ('ppac environment list').";

    // How long to wait for an accepted add/remove to show up, and how often to check.
    private const int DefaultWaitSeconds = 120;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Waits between membership checks. Tests replace this so they do not sleep.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "create" },
            Description = "Create an empty environment group. Refuses a name that is already in use unless --allow-duplicate is given. Use --whatif to preview the request.",
            Class = CommandClass.Write,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environmentGroups?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = WritePermission,
            Options = new[]
            {
                new OptionSpec("--name", Array.Empty<string>(), OptionType.String,
                    "Display name of the new group.", Required: true),
                new OptionSpec("--description", Array.Empty<string>(), OptionType.String,
                    "Description of the group. Required: the service rejects a group without one.", Required: true),
                new OptionSpec("--allow-duplicate", Array.Empty<string>(), OptionType.Bool,
                    "Create the group even if another group already has the same name (the service allows it)."),
                WriteSafety.WhatIfOption,
                JsonOutput.Option
            },
            Handler = CreateAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "delete" },
            Description = "Delete an environment group. Asks for confirmation unless --yes is given; use --whatif to preview.",
            Class = CommandClass.Destructive,
            Status = VerificationStatus.Verified,
            Api = $"DELETE /environmentmanagement/environmentGroups/{{groupId}}?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = WritePermission,
            Options = new[]
            {
                new OptionSpec("--group", Array.Empty<string>(), OptionType.String,
                    "Group ID, as shown by 'ppac environment group list'.", Required: true),
                WriteSafety.WhatIfOption,
                WriteSafety.YesOption,
                JsonOutput.Option
            },
            Handler = DeleteAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "add-environment" },
            Description = "Add an environment to an environment group (the group's rules then apply to it). Waits until the environment shows up in the group unless --no-wait is given.",
            Class = CommandClass.Write,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environmentGroups/{{groupId}}/addEnvironment/{{environmentId}}?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = WritePermission,
            Options = MembershipOptions(destructive: false),
            Handler = AddEnvironmentAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "group", "remove-environment" },
            Description = "Remove an environment from an environment group. The environment keeps the configuration the group applied, but its settings and policies are unlocked. Asks for confirmation unless --yes is given.",
            Class = CommandClass.Destructive,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environmentGroups/{{groupId}}/removeEnvironment/{{environmentId}}?api-version={EnvironmentGroupApi.ApiVersion}",
            Permission = WritePermission,
            Options = MembershipOptions(destructive: true),
            Handler = RemoveEnvironmentAsync
        };
    }

    private static OptionSpec[] MembershipOptions(bool destructive)
    {
        var options = new List<OptionSpec>
        {
            new("--group", Array.Empty<string>(), OptionType.String,
                "Group ID, as shown by 'ppac environment group list'.", Required: true),
            new("--environment", Array.Empty<string>(), OptionType.String,
                "Environment ID, as shown by 'ppac environment list'.", Required: true),
            new("--no-wait", Array.Empty<string>(), OptionType.Bool,
                "Return as soon as the service accepts the request, without waiting for the change to show up."),
            new("--timeout", Array.Empty<string>(), OptionType.Int,
                $"Seconds to wait for the change to show up. Default: {DefaultWaitSeconds}."),
            WriteSafety.WhatIfOption
        };
        if (destructive)
        {
            options.Add(WriteSafety.YesOption);
        }
        options.Add(JsonOutput.Option);
        return options.ToArray();
    }

    // ---- create ----

    private static Task<int> CreateAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var name = ctx.GetString("--name");
        var description = ctx.GetString("--description");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            Console.Error.WriteLine("--name and --description must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        name = name.Trim();
        description = description.Trim();
        var allowDuplicate = ctx.GetBool("--allow-duplicate");
        var json = ctx.GetBool(JsonOutput.OptionName);

        if (ctx.GetBool(WriteSafety.WhatIfName))
        {
            // --whatif for create is local: no sign-in and no request.
            var profile = ProfileStore.GetActive();
            var cloud = profile is null ? CloudConfig.Commercial : CloudConfig.Get(profile.Cloud);

            WriteSafety.WriteWhatIf(
                "environment group create",
                $"create an empty environment group named '{name}'.",
                EnvironmentGroupApi.CreateRequest(cloud, name, description),
                json,
                new[]
                {
                    allowDuplicate
                        ? "Duplicate names are allowed (--allow-duplicate)."
                        : "A real run first checks that no existing group has this name (the service itself allows duplicate names)."
                });
            return Task.FromResult(ExitCodes.Success);
        }

        return RunAsync(
            (api, cloud) => CreateCoreAsync(api, cloud, name, description, allowDuplicate, json, cancellationToken),
            json,
            cancellationToken);
    }

    internal static async Task<int> CreateCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, string name, string description, bool allowDuplicate, bool json,
        CancellationToken cancellationToken)
    {
        // The service allows several groups with the same name (seen live), which is confusing in the admin
        // center and in scripts, so refuse by default and say which group already has the name.
        if (!allowDuplicate)
        {
            var sameName = (await EnvironmentGroupApi.ListAsync(api, cloud, null, cancellationToken))
                .Where(g => string.Equals(JsonOutput.Str(g, "displayName")?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                .Select(g => JsonOutput.Str(g, "id") ?? "-")
                .ToList();
            if (sameName.Count > 0)
            {
                return ErrorReport.Write(
                    ExitCodes.Failure,
                    $"A group named '{name}' already exists (ID {string.Join(", ", sameName)}). Nothing was created.",
                    json,
                    hint: "Use a different name, or add --allow-duplicate to create another group with the same name.",
                    details: new JsonObject
                    {
                        ["duplicate"] = true,
                        ["existingGroupIds"] = new JsonArray(sameName.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
                    });
            }
        }

        var group = await api.SendAsync(EnvironmentGroupApi.CreateRequest(cloud, name, description), cancellationToken);

        if (json)
        {
            JsonOutput.Write(group ?? new JsonObject { ["created"] = true });
        }
        else if (group is null)
        {
            Console.WriteLine($"The service accepted the request to create group '{name}' but returned no details.");
            Console.WriteLine("Run 'ppac environment group list' to see it.");
        }
        else
        {
            Console.WriteLine("Group created.");
            Console.WriteLine($"Name:     {JsonOutput.Str(group, "displayName") ?? name}");
            Console.WriteLine($"Group ID: {JsonOutput.Str(group, "id") ?? "-"}");
        }
        return ExitCodes.Success;
    }

    // ---- delete ----

    private static Task<int> DeleteAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var groupId = ctx.GetString("--group");
        if (string.IsNullOrWhiteSpace(groupId))
        {
            Console.Error.WriteLine("--group must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var whatIf = ctx.GetBool(WriteSafety.WhatIfName);
        var yes = ctx.GetBool(WriteSafety.YesName);
        var json = ctx.GetBool(JsonOutput.OptionName);
        var interactive = WriteSafety.IsInteractive();

        // Fail fast, before signing in, when a prompt is needed but nobody can answer it.
        if (!whatIf && !yes && !interactive)
        {
            WriteSafety.WriteRefusal("Deleting a group");
            return Task.FromResult(ExitCodes.Usage);
        }

        return RunAsync(
            (api, cloud) => DeleteCoreAsync(
                api, cloud, groupId.Trim(), whatIf, yes, json, interactive, Console.In, cancellationToken),
            json,
            cancellationToken);
    }

    internal static async Task<int> DeleteCoreAsync(
        PowerPlatformApi api,
        CloudConfig cloud,
        string groupId,
        bool whatIf,
        bool yes,
        bool json,
        bool interactive,
        TextReader input,
        CancellationToken cancellationToken)
    {
        // Look the group up first so --whatif and the prompt can name it, and so an unknown ID is
        // reported as not found instead of a possibly misleading success (the service answers 204
        // for an unknown ID on get).
        var group = await EnvironmentGroupApi.GetAsync(api, cloud, groupId, cancellationToken);
        if (group is null)
        {
            return ErrorReport.Write(
                ExitCodes.NotFound,
                $"Group {groupId} was not found. 'ppac environment group list' shows the group IDs.",
                json,
                details: new JsonObject { ["lookup"] = "notFound", ["whatIf"] = whatIf });
        }

        var name = JsonOutput.Str(group, "displayName");
        var groupText = name is null ? $"group {groupId}" : $"group '{name}' ({groupId})";
        var request = EnvironmentGroupApi.DeleteRequest(cloud, groupId);
        var summary = $"delete {groupText}.";

        // The service refuses to delete a group that still holds environments (400 "Request failed with status
        // code Conflict", verified live 2026-10-30), and the message does not say why. Check first.
        // One read can miss a member (live, the filter missed a member in 6 of 20 reads), so the list is the union of
        // several reads: an environment seen in any of them blocks the delete.
        var members = await EnvironmentGroupApi.SampleMembersAsync(api, cloud, groupId, cancellationToken);
        var memberList = members is null
            ? new List<(string Id, string Name)>()
            : members.Union();

        if (whatIf)
        {
            var lines = new List<string>
            {
                $"Created: {JsonOutput.Str(group, "createdTime") ?? "-"}",
                members is null
                    ? "Could not check whether the group holds environments (the service did not accept the membership filter). The service refuses to delete a group that holds environments."
                    : memberList.Count == 0
                        ? "The group holds no environments."
                        : $"The group still holds {memberList.Count} environment(s): {string.Join(", ", memberList.Select(m => $"{m.Name} ({m.Id})"))}. The delete would be refused until they are removed."
            };
            var details = new JsonObject
            {
                ["lookup"] = "found",
                ["group"] = group.DeepClone(),
                ["environmentCount"] = members is null ? null : memberList.Count
            };
            WriteSafety.WriteWhatIf("environment group delete", summary, request, json, lines, details);
            return ExitCodes.Success;
        }

        if (memberList.Count > 0)
        {
            var removeCommands = string.Join(" ; ", memberList.Select(m =>
                $"ppac environment group remove-environment --group {groupId} --environment {m.Id}"));
            return ErrorReport.Write(
                ExitCodes.Failure,
                $"{char.ToUpperInvariant(groupText[0])}{groupText[1..]} still holds {memberList.Count} environment(s): " +
                $"{string.Join(", ", memberList.Select(m => $"{m.Name} ({m.Id})"))}. A group with environments cannot be deleted. Nothing was deleted.",
                json,
                hint: $"Remove the environments first: {removeCommands}. If you have just removed them, wait a few minutes and try again: the service can keep reporting the old membership for several minutes after a change (seen up to about 6 minutes).",
                details: new JsonObject
                {
                    ["groupHasEnvironments"] = true,
                    ["environments"] = new JsonArray(memberList
                        .Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id, ["displayName"] = m.Name })
                        .ToArray())
                });
        }

        switch (WriteSafety.Confirm(char.ToUpperInvariant(summary[0]) + summary[1..], yes, interactive, input, Console.Error))
        {
            case ConfirmResult.Refused:
                WriteSafety.WriteRefusal("Deleting a group");
                return ExitCodes.Usage;
            case ConfirmResult.Declined:
                return ErrorReport.Write(ExitCodes.Declined, "Cancelled. Nothing was deleted.", json);
        }

        try
        {
            await api.SendAsync(request, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                                      ex.Message.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
        {
            // Live (2026-10-30) a group that holds an environment answers "400 ... status code Conflict" and nothing
            // changes. The membership reads above did not show one, so say what the refusal most likely means.
            return ErrorReport.Write(
                ExitCodes.Failure,
                $"The service refused to delete {groupText}: \"{ex.Message}\". It does this when the group still holds an environment. Nothing was deleted.",
                json,
                hint: $"Check with: ppac environment list --filter \"environmentGroupId eq '{groupId}'\" (the service can answer inconsistently, so run it more than once). " +
                      $"Then remove any environment: ppac environment group remove-environment --group {groupId} --environment <environment id>",
                details: new JsonObject { ["refusedByService"] = true, ["status"] = 400 });
        }

        if (json)
        {
            JsonOutput.Write(new JsonObject { ["deleted"] = true, ["groupId"] = groupId });
        }
        else
        {
            Console.WriteLine($"Deleted {groupText}.");
        }
        return ExitCodes.Success;
    }


    // ---- add / remove environment ----

    private static Task<int> AddEnvironmentAsync(CommandContext ctx, CancellationToken cancellationToken) =>
        MembershipAsync(ctx, remove: false, cancellationToken);

    private static Task<int> RemoveEnvironmentAsync(CommandContext ctx, CancellationToken cancellationToken) =>
        MembershipAsync(ctx, remove: true, cancellationToken);

    private static Task<int> MembershipAsync(CommandContext ctx, bool remove, CancellationToken cancellationToken)
    {
        var groupId = ctx.GetString("--group");
        var environmentId = ctx.GetString("--environment");
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(environmentId))
        {
            Console.Error.WriteLine("--group and --environment must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var timeout = ctx.GetInt("--timeout") ?? DefaultWaitSeconds;
        if (timeout < 1 || timeout > 3600)
        {
            Console.Error.WriteLine("--timeout must be between 1 and 3600 seconds.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var whatIf = ctx.GetBool(WriteSafety.WhatIfName);
        var noWait = ctx.GetBool("--no-wait");
        var yes = remove && ctx.GetBool(WriteSafety.YesName);
        var json = ctx.GetBool(JsonOutput.OptionName);
        var interactive = WriteSafety.IsInteractive();

        // Fail fast, before signing in, when a prompt is needed but nobody can answer it.
        if (remove && !whatIf && !yes && !interactive)
        {
            WriteSafety.WriteRefusal("Removing an environment from a group");
            return Task.FromResult(ExitCodes.Usage);
        }

        var request = new MembershipChange(remove, groupId.Trim(), environmentId.Trim(), whatIf, yes, noWait, timeout, json, interactive);
        return RunAsync(
            (api, cloud) => MembershipCoreAsync(api, cloud, request, Console.In, cancellationToken),
            json,
            cancellationToken);
    }

    internal sealed record MembershipChange(
        bool Remove, string GroupId, string EnvironmentId, bool WhatIf, bool Yes, bool NoWait,
        int TimeoutSeconds, bool Json, bool Interactive);

    internal static async Task<int> MembershipCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, MembershipChange change, TextReader input, CancellationToken cancellationToken)
    {
        var verb = change.Remove ? "remove" : "add";
        var command = change.Remove ? "environment group remove-environment" : "environment group add-environment";

        // Look both up first so the preview and the prompt can name them, and so a wrong ID is a clear
        // "not found" (the service answers 204 for an unknown group on get; an unknown environment is a 404).
        var group = await EnvironmentGroupApi.GetAsync(api, cloud, change.GroupId, cancellationToken);
        if (group is null)
        {
            return ErrorReport.Write(
                ExitCodes.NotFound,
                $"Group {change.GroupId} was not found. 'ppac environment group list' shows the group IDs.",
                change.Json,
                details: new JsonObject { ["lookup"] = "notFound", ["whatIf"] = change.WhatIf });
        }

        var environment = await EnvironmentApi.GetAsync(api, cloud, change.EnvironmentId, "displayName,type,state", cancellationToken);

        var groupName = JsonOutput.Str(group, "displayName");
        var environmentName = JsonOutput.Str(environment, "displayName");
        var environmentType = JsonOutput.Str(environment, "type");
        var groupText = groupName is null ? $"group {change.GroupId}" : $"group '{groupName}' ({change.GroupId})";
        var environmentText = environmentName is null
            ? $"environment {change.EnvironmentId}"
            : $"environment '{environmentName}' ({(environmentType is null ? "" : environmentType + ", ")}{change.EnvironmentId})";

        var request = change.Remove
            ? EnvironmentGroupApi.RemoveEnvironmentRequest(cloud, change.GroupId, change.EnvironmentId)
            : EnvironmentGroupApi.AddEnvironmentRequest(cloud, change.GroupId, change.EnvironmentId);
        var summary = change.Remove
            ? $"remove {environmentText} from {groupText}."
            : $"add {environmentText} to {groupText}.";

        // Already in the desired state? Say so instead of sending the request again.
        // The service can answer the same read differently from one moment to the next (live, 2026-10-30), so
        // "already done" is only believed when several reads in a row agree; otherwise the request is sent.
        var samples = await EnvironmentGroupApi.SampleMembersAsync(api, cloud, change.GroupId, cancellationToken);
        var before = samples?.Verdict(change.EnvironmentId);
        var alreadyDone = change.Remove
            ? before == EnvironmentGroupApi.MembershipVerdict.NotMember
            : before == EnvironmentGroupApi.MembershipVerdict.Member;
        var unclearText = samples is null
            ? null
            : $"{samples.ReadsWith(change.EnvironmentId)} of {samples.Count} reads list the environment in the group";
        if (alreadyDone)
        {
            var done = change.Remove
                ? $"{char.ToUpperInvariant(environmentText[0])}{environmentText[1..]} is not in {groupText}."
                : $"{char.ToUpperInvariant(environmentText[0])}{environmentText[1..]} is already in {groupText}.";
            if (change.Remove)
            {
                return ErrorReport.Write(
                    ExitCodes.NotFound, done, change.Json,
                    details: new JsonObject { ["membership"] = "notAMember", ["whatIf"] = change.WhatIf });
            }
            if (change.Json)
            {
                JsonOutput.Write(new JsonObject
                {
                    ["alreadyInGroup"] = true,
                    ["groupId"] = change.GroupId,
                    ["environmentId"] = change.EnvironmentId
                });
            }
            else
            {
                Console.WriteLine(done + " Nothing to do.");
            }
            return ExitCodes.Success;
        }

        if (change.WhatIf)
        {
            var lines = new List<string>
            {
                before switch
                {
                    null => "Current membership could not be checked (the service did not accept the membership filter).",
                    EnvironmentGroupApi.MembershipVerdict.Member => "Current membership: in the group.",
                    EnvironmentGroupApi.MembershipVerdict.NotMember => "Current membership: not in the group.",
                    _ => $"Current membership is unclear: {unclearText} (the service answers inconsistently). The request would be sent."
                }
            };
            lines.Add(change.Remove
                ? "The environment keeps the configuration the group applied, but its settings and policies are unlocked."
                : "The group's rules apply to the environment once it is added. A group can only hold managed environments with Dataverse (per the admin center documentation).");
            var details = new JsonObject
            {
                ["group"] = group.DeepClone(),
                ["environment"] = environment.DeepClone(),
                ["membership"] = before switch
                {
                    null => "unknown",
                    EnvironmentGroupApi.MembershipVerdict.Member => "member",
                    EnvironmentGroupApi.MembershipVerdict.NotMember => "notMember",
                    _ => "unclear"
                }
            };
            WriteSafety.WriteWhatIf(command, summary, request, change.Json, lines, details);
            return ExitCodes.Success;
        }

        if (before == EnvironmentGroupApi.MembershipVerdict.Unclear)
        {
            Console.Error.WriteLine(
                $"Repeated reads of the group's members disagreed ({unclearText}), so ppac is sending the request instead of assuming it is already done.");
        }

        if (change.Remove)
        {
            switch (WriteSafety.Confirm(char.ToUpperInvariant(summary[0]) + summary[1..], change.Yes, change.Interactive, input, Console.Error))
            {
                case ConfirmResult.Refused:
                    WriteSafety.WriteRefusal("Removing an environment from a group");
                    return ExitCodes.Usage;
                case ConfirmResult.Declined:
                    return ErrorReport.Write(ExitCodes.Declined, "Cancelled. Nothing was changed.", change.Json);
            }
        }

        var response = await api.SendDetailedAsync(request, cancellationToken);
        var operation = response.Header("Operation-Location") ?? response.Header("Location");

        // The reference pages document 202 Accepted (and 204) with no body or headers, so the change may
        // not be visible yet. Unless told not to, wait for the membership to match.
        string membership;
        if (change.NoWait)
        {
            membership = "notChecked";
        }
        else
        {
            if (!change.Json)
            {
                Console.Error.WriteLine(
                    $"HTTP {(int)response.Status}: waiting up to {change.TimeoutSeconds} seconds for the change to show up (use --no-wait to skip)...");
            }
            membership = await WaitForMembershipAsync(
                api, cloud, change.GroupId, change.EnvironmentId, expected: !change.Remove, change.TimeoutSeconds, cancellationToken);
        }

        if (membership == "timeout")
        {
            return ErrorReport.Write(
                ExitCodes.Failure,
                $"The service accepted the request to {verb} {environmentText} {(change.Remove ? "from" : "to")} {groupText} (HTTP {(int)response.Status}), " +
                $"but the change was not visible after {change.TimeoutSeconds} seconds. Check later with 'ppac environment list --filter \"environmentGroupId eq '{change.GroupId}'\"'.",
                change.Json,
                details: new JsonObject
                {
                    ["accepted"] = true,
                    ["status"] = (int)response.Status,
                    ["membership"] = "timeout",
                    ["operationLocation"] = operation
                });
        }

        if (change.Json)
        {
            var result = new JsonObject
            {
                ["accepted"] = true,
                ["status"] = (int)response.Status,
                ["groupId"] = change.GroupId,
                ["environmentId"] = change.EnvironmentId,
                ["membership"] = membership
            };
            if (operation is not null)
            {
                result["operationLocation"] = operation;
            }
            JsonOutput.Write(result);
        }
        else
        {
            var past = change.Remove ? $"Removed {environmentText} from {groupText}." : $"Added {environmentText} to {groupText}.";
            Console.WriteLine(membership switch
            {
                "confirmed" => past,
                "unknown" => $"The service accepted the request (HTTP {(int)response.Status}) but membership could not be checked, so the result is unconfirmed.",
                _ => $"The service accepted the request (HTTP {(int)response.Status}). It was not checked whether the change has taken effect."
            });
            if (operation is not null)
            {
                Console.WriteLine($"Operation: {operation}");
            }
        }
        return ExitCodes.Success;
    }

    /// <summary>"confirmed", "timeout", or "unknown" (the membership filter is not accepted).</summary>
    private static async Task<string> WaitForMembershipAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, string environmentId,
        bool expected, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var limit = TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var current = await EnvironmentGroupApi.IsMemberAsync(api, cloud, groupId, environmentId, cancellationToken);
            if (current is null)
            {
                return "unknown";
            }
            if (current == expected)
            {
                return "confirmed";
            }
            if (waited >= limit)
            {
                return "timeout";
            }
            await Delay(PollInterval, cancellationToken);
            waited += PollInterval;
        }
    }

    private static Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call, bool json, CancellationToken cancellationToken) =>
        ApiCommandRunner.RunAsync(call, ForbiddenHint, NotFoundHint, cancellationToken, json);
}
