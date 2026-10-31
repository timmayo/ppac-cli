using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;
using Ppac.Cli.Safety;

namespace Ppac.Cli.Commands;

/// <summary>
/// 'ppac environment managed enable | disable'. Source: Environment Management API, "Environment Managed
/// Governance" (api-version 2024-10-01). Both calls answer 202 Accepted with no documented body or headers,
/// so by default the command waits until the environment's protectionLevel shows the change. The reference
/// pages list only the .default scope; the delegated permission the call needs is not documented.
/// Admin center documentation: only the Power Platform Administrator or Dynamics 365 Administrator role in
/// Microsoft Entra ID can enable or edit Managed Environments (Delegated Admin and Environment Admin cannot).
/// Verified live 2026-10-30 on a sandbox: enable returned 202 with an Operation-Location header and protectionLevel
/// changed Basic to Standard in about 5 seconds; disable took about 27 seconds. ValidateOnly is not supported by the service.
/// </summary>
public static class EnvironmentManagedCommands
{
    private const string ForbiddenHint =
        "Changing Managed Environments needs the Power Platform Administrator or Dynamics 365 Administrator role " +
        "in Microsoft Entra ID (Delegated Admin and Environment Admin cannot change it, per the admin center documentation). " +
        "The API reference lists only the .default scope for this call, so which delegated permission it needs is not documented. " +
        "If you added permissions to the app registration, sign in again ('ppac auth create').";

    private const string NotFoundHint = "Check the environment ID ('ppac environment list').";

    private const int DefaultWaitSeconds = 120;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Waits between state checks. Tests replace this so they do not sleep.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "managed", "enable" },
            Description = "Turn on Managed Environments for an environment. Use --whatif to preview it. Waits until the change shows up unless --no-wait is given.",
            Class = CommandClass.Write,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environments/{{environmentId}}/governanceSetting/enableManaged?api-version={EnvironmentManagedApi.ApiVersion}",
            Options = Options(destructive: false),
            Handler = (ctx, ct) => RunCommandAsync(ctx, disable: false, ct)
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "managed", "disable" },
            Description = "Turn off Managed Environments for an environment. Make sure no Managed Environments capabilities are in use first. Asks for confirmation unless --yes is given.",
            Class = CommandClass.Destructive,
            Status = VerificationStatus.Verified,
            Api = $"POST /environmentmanagement/environments/{{environmentId}}/governanceSetting/disableManaged?api-version={EnvironmentManagedApi.ApiVersion}",
            Options = Options(destructive: true),
            Handler = (ctx, ct) => RunCommandAsync(ctx, disable: true, ct)
        };
    }

    private static OptionSpec[] Options(bool destructive)
    {
        var options = new List<OptionSpec>
        {
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

    internal sealed record ManagedChange(
        bool Disable, string EnvironmentId, bool WhatIf, bool Yes, bool NoWait,
        int TimeoutSeconds, bool Json, bool Interactive);

    private static Task<int> RunCommandAsync(CommandContext ctx, bool disable, CancellationToken cancellationToken)
    {
        var environmentId = ctx.GetString("--environment");
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            Console.Error.WriteLine("--environment must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var timeout = ctx.GetInt("--timeout") ?? DefaultWaitSeconds;
        if (timeout < 1 || timeout > 3600)
        {
            Console.Error.WriteLine("--timeout must be between 1 and 3600 seconds.");
            return Task.FromResult(ExitCodes.Usage);
        }

        var whatIf = ctx.GetBool(WriteSafety.WhatIfName);
        var yes = disable && ctx.GetBool(WriteSafety.YesName);
        var json = ctx.GetBool(JsonOutput.OptionName);
        var interactive = WriteSafety.IsInteractive();

        // --whatif changes nothing, so it never needs a prompt.
        if (disable && !whatIf && !yes && !interactive)
        {
            WriteSafety.WriteRefusal("Turning off Managed Environments");
            return Task.FromResult(ExitCodes.Usage);
        }

        var change = new ManagedChange(
            disable, environmentId.Trim(), whatIf, yes, ctx.GetBool("--no-wait"), timeout, json, interactive);
        return ApiCommandRunner.RunAsync(
            (api, cloud) => RunCoreAsync(api, cloud, change, Console.In, cancellationToken),
            ForbiddenHint, NotFoundHint, cancellationToken, json);
    }

    internal static async Task<int> RunCoreAsync(
        PowerPlatformApi api, CloudConfig cloud, ManagedChange change, TextReader input, CancellationToken cancellationToken)
    {
        var command = change.Disable ? "environment managed disable" : "environment managed enable";
        var verb = change.Disable ? "turn off" : "turn on";

        // Look the environment up first so the preview and the prompt can name it, and so a wrong ID is a
        // clear "not found" (a 404 from the service).
        var environment = await EnvironmentApi.GetAsync(api, cloud, change.EnvironmentId, null, cancellationToken);
        var name = JsonOutput.Str(environment, "displayName");
        var type = JsonOutput.Str(environment, "type");
        var environmentText = name is null
            ? $"environment {change.EnvironmentId}"
            : $"environment '{name}' ({(type is null ? "" : type + ", ")}{change.EnvironmentId})";
        // The service can answer the same read differently from one moment to the next (live, 2026-10-30), so
        // "already in that state" is only believed when several reads in a row agree.
        var readState = await EnvironmentManagedApi.SampleAsync(api, cloud, change.EnvironmentId, environment, cancellationToken);
        var level = JsonOutput.Str(environment, "protectionLevel");

        var request = change.Disable
            ? EnvironmentManagedApi.DisableRequest(cloud, change.EnvironmentId)
            : EnvironmentManagedApi.EnableRequest(cloud, change.EnvironmentId);
        var summary = $"{verb} Managed Environments for {environmentText}.";

        // Already in the desired state? Say so instead of sending the request again.
        var desired = change.Disable
            ? EnvironmentManagedApi.ReadState.NotManaged
            : EnvironmentManagedApi.ReadState.Managed;
        if (readState == desired)
        {
            var done = $"{char.ToUpperInvariant(environmentText[0])}{environmentText[1..]} is " +
                       (change.Disable ? "not managed" : "already managed") + ". Nothing to do.";
            if (change.Json)
            {
                JsonOutput.Write(new JsonObject
                {
                    ["alreadyInState"] = true,
                    ["environmentId"] = change.EnvironmentId,
                    ["managed"] = !change.Disable,
                    ["protectionLevel"] = level
                });
            }
            else
            {
                Console.WriteLine(done);
            }
            return ExitCodes.Success;
        }

        if (change.WhatIf)
        {
            var lines = new List<string>
            {
                readState switch
                {
                    EnvironmentManagedApi.ReadState.Managed => $"Current state: managed (protectionLevel {level}).",
                    EnvironmentManagedApi.ReadState.NotManaged => $"Current state: not managed (protectionLevel {level}).",
                    EnvironmentManagedApi.ReadState.Mixed =>
                        $"Current state is unclear: {EnvironmentManagedApi.ReadSamples} repeated reads disagreed (the service answers inconsistently). The request would be sent.",
                    _ => "Current state could not be read (the environment has no protectionLevel)."
                }
            };
            lines.Add(change.Disable
                ? "Before turning it off, make sure no Managed Environments capabilities are in use (per the admin center documentation)."
                : "Managed Environments needs Dataverse on production, sandbox and trial environments (developer environments do not need it), and a license (per the admin center documentation).");
            var details = new JsonObject
            {
                ["environment"] = environment.DeepClone(),
                ["managed"] = readState switch
                {
                    EnvironmentManagedApi.ReadState.Managed => true,
                    EnvironmentManagedApi.ReadState.NotManaged => false,
                    _ => null
                },
                ["stateReads"] = readState.ToString()
            };
            WriteSafety.WriteWhatIf(command, summary, request, change.Json, lines, details);
            return ExitCodes.Success;
        }

        if (readState == EnvironmentManagedApi.ReadState.Mixed)
        {
            Console.Error.WriteLine(
                $"Repeated reads of the environment's state disagreed, so ppac is sending the request instead of assuming it is already done.");
        }

        if (change.Disable)
        {
            switch (WriteSafety.Confirm(char.ToUpperInvariant(summary[0]) + summary[1..], change.Yes, change.Interactive, input, Console.Error))
            {
                case ConfirmResult.Refused:
                    WriteSafety.WriteRefusal("Turning off Managed Environments");
                    return ExitCodes.Usage;
                case ConfirmResult.Declined:
                    return ErrorReport.Write(ExitCodes.Declined, "Cancelled. Nothing was changed.", change.Json);
            }
        }

        var response = await api.SendDetailedAsync(request, cancellationToken);
        var operation = response.Header("Operation-Location") ?? response.Header("Location");

        string state;
        if (change.NoWait)
        {
            state = "notChecked";
        }
        else
        {
            if (!change.Json)
            {
                Console.Error.WriteLine(
                    $"HTTP {(int)response.Status}: waiting up to {change.TimeoutSeconds} seconds for the change to show up (use --no-wait to skip)...");
            }
            state = await WaitForStateAsync(
                api, cloud, change.EnvironmentId, expectedManaged: !change.Disable, change.TimeoutSeconds, cancellationToken);
        }

        if (state == "timeout")
        {
            return ErrorReport.Write(
                ExitCodes.Failure,
                $"The service accepted the request to {verb} Managed Environments for {environmentText} (HTTP {(int)response.Status}), " +
                $"but the change was not visible after {change.TimeoutSeconds} seconds. Check later with 'ppac environment get --environment {change.EnvironmentId}' (protectionLevel).",
                change.Json,
                details: new JsonObject
                {
                    ["accepted"] = true,
                    ["status"] = (int)response.Status,
                    ["state"] = "timeout",
                    ["operationLocation"] = operation
                });
        }

        if (change.Json)
        {
            var result = new JsonObject
            {
                ["accepted"] = true,
                ["status"] = (int)response.Status,
                ["environmentId"] = change.EnvironmentId,
                ["state"] = state
            };
            if (operation is not null)
            {
                result["operationLocation"] = operation;
            }
            JsonOutput.Write(result);
        }
        else
        {
            var past = change.Disable
                ? $"Managed Environments is now off for {environmentText}."
                : $"Managed Environments is now on for {environmentText}.";
            Console.WriteLine(state switch
            {
                "confirmed" => past,
                "unknown" => $"The service accepted the request (HTTP {(int)response.Status}) but the environment's state could not be read, so the result is unconfirmed.",
                _ => $"The service accepted the request (HTTP {(int)response.Status}). It was not checked whether the change has taken effect."
            });
            if (operation is not null)
            {
                Console.WriteLine($"Operation: {operation}");
            }
        }
        return ExitCodes.Success;
    }

    /// <summary>"confirmed", "timeout", or "unknown" (the environment has no protectionLevel).</summary>
    private static async Task<string> WaitForStateAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId,
        bool expectedManaged, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var limit = TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var current = EnvironmentManagedApi.IsManaged(
                await EnvironmentApi.GetAsync(api, cloud, environmentId, null, cancellationToken));
            if (current is null)
            {
                return "unknown";
            }
            if (current == expectedManaged)
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
}
