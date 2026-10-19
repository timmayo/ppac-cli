using System.CommandLine;
using System.Net;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Environments;
using Ppac.Core.Http;
using Ppac.Core.Operations;
using Ppac.Core.Output;
using Ppac.Core.Safety;

namespace Ppac.Cli.Commands;

public sealed record ResolvedEnvironment(string Id, string Name);

public sealed record WaitSettings(TimeSpan Interval, TimeSpan Timeout);

/// <summary>Shared pieces for commands that change things.</summary>
public static class WriteHelpers
{
    public const string EnvironmentsPath = "/environmentmanagement/environments";
    public const string ApiVersion = "api-version=2024-10-01";

    // A new Option instance per command, so the same flag can be declared on several commands.
    public static Option<bool> YesOption() =>
        new("--yes", "-y") { Description = "Do not ask for confirmation (required when not interactive)" };

    public static Option<bool> WhatIfOption() =>
        new("--whatif") { Description = "Check the request without changing anything" };

    public static Option<bool> WaitOption() =>
        new("--wait") { Description = "Wait for the operation to finish (exit 0 on success, 8 if it failed, 9 on timeout)" };

    public static Option<string> TimeoutOption() =>
        new("--timeout") { Description = "With --wait: give up after this long, for example 30m (the operation keeps running)", DefaultValueFactory = _ => "30m" };

    public static Option<string> IntervalOption() =>
        new("--interval") { Description = "With --wait: how often to check, for example 10s", DefaultValueFactory = _ => "10s" };

    public static Confirmation CreateConfirmation() =>
        new(Console.In, Console.Error, interactive: !Console.IsInputRedirected);

    public static string EnvPath(string environmentId, string suffix = "") =>
        $"{EnvironmentsPath}/{Uri.EscapeDataString(environmentId)}{suffix}";

    /// <summary>Reads --timeout and --interval when --wait is set, so a bad value fails before anything is changed.</summary>
    public static WaitSettings? ParseWait(ParseResult parse, Option<bool> wait, Option<string> timeout, Option<string> interval)
    {
        if (!parse.GetValue(wait)) return null;

        var pollEvery = DurationParser.Parse(parse.GetValue(interval)!, "--interval");
        var giveUpAfter = DurationParser.Parse(parse.GetValue(timeout)!, "--timeout");
        if (pollEvery <= TimeSpan.Zero)
            throw new PpacException(ExitCodes.Usage, "--interval must be greater than zero.");
        return new WaitSettings(pollEvery, giveUpAfter);
    }

    public static async Task<ResolvedEnvironment> ResolveEnvironmentAsync(PpApiClient client, string idOrName, CancellationToken ct)
    {
        // A GUID is fetched directly. Live runs showed the list can omit an environment from one call to the
        // next (a failed create), while a direct GET answers consistently and is one request, not a full list.
        // Other IDs (such as Default-<tenant GUID>) and display names still go through the list.
        var text = idOrName.Trim();
        if (Guid.TryParse(text, out var guid))
        {
            var id = guid.ToString();
            try
            {
                var response = await client.SendAsync(HttpMethod.Get, $"{EnvPath(id)}?{ApiVersion}", null, ct);
                var env = response.Json as JsonObject
                    ?? throw new PpacException(ExitCodes.General, "The service returned an empty response for the environment.");
                var name = OutputWriter.CellText(env, "displayName");
                return new ResolvedEnvironment(OutputWriter.CellText(env, "id") is { Length: > 0 } realId ? realId : id,
                    name.Length > 0 ? name : id);
            }
            catch (PpacException ex) when (ex.ExitCode == ExitCodes.NotFound)
            {
                throw new PpacException(ExitCodes.NotFound, $"Environment '{text}' not found.");
            }
        }

        var rows = await client.GetAllAsync($"{EnvironmentsPath}?{ApiVersion}", ct);
        var found = EnvironmentLookup.Find(rows, idOrName);
        return new ResolvedEnvironment(OutputWriter.CellText(found, "id"), OutputWriter.CellText(found, "displayName"));
    }

    /// <summary>
    /// Reports the result of a validation-only request. A 202 is flagged because a 202 could mean the service
    /// started the work. (A live delete --whatif answered with something other than 202 and changed nothing.)
    /// </summary>
    public static void ReportValidation(ApiResponse response, string wouldDo, string checkHint)
    {
        if (response.Status == HttpStatusCode.Accepted)
            Console.Error.WriteLine(
                "WARNING: the service answered 202 Accepted to a validation-only request, which may mean it started the work. " +
                $"Check whether anything started with: {checkHint}");
        else
            Console.Error.WriteLine($"Validation passed. Nothing was changed. Would {wouldDo}.");
    }

    /// <summary>
    /// Reports an accepted (202) request and returns the operation ID. A live 202 had an empty body and no
    /// Operation-Location header; the operation URL was in the Location header. With quiet, the details go to
    /// stderr only, so stdout carries just the final operation when the caller then waits.
    /// </summary>
    public static string? WriteAccepted(ParseResult parse, string action, ResolvedEnvironment environment,
        ApiResponse response, OutputFormat format, bool quiet)
    {
        string? operationLocation = null;
        if (response.Headers.TryGetValue("Operation-Location", out var op)) operationLocation = op;
        string? location = null;
        if (response.Headers.TryGetValue("Location", out var loc)) location = loc;

        var operationId = OperationLocation.ParseOperationId(operationLocation) ?? OperationLocation.ParseOperationId(location);

        if (!quiet)
        {
            var row = new JsonObject
            {
                ["action"] = action,
                ["environmentId"] = environment.Id,
                ["environmentName"] = environment.Name,
                ["httpStatus"] = (int)response.Status,
            };
            if (operationId is not null) row["operationId"] = operationId;
            if (operationLocation is not null) row["operationLocation"] = operationLocation;
            if (location is not null) row["location"] = location;
            if (response.Json is not null) row["response"] = response.Json.DeepClone();
            else if (!string.IsNullOrWhiteSpace(response.Body)) row["responseBody"] = response.Body;

            ObjectWriter.Write(Console.Out, row, format);
        }

        if (parse.GetValue(GlobalOptions.Verbose))
            foreach (var header in response.Headers)
                Console.Error.WriteLine($"{header.Key}: {header.Value}");

        Console.Error.WriteLine(operationId is not null
            ? $"Accepted. Track progress with: ppac op wait {environment.Id} {operationId}"
            : $"Accepted, but the service gave no operation ID. Look for it with: ppac op list {environment.Id}");

        return operationId;
    }

    /// <summary>Waits for the operation returned by an accepted request and prints the final operation.</summary>
    public static async Task WaitAfterAcceptedAsync(PpApiClient client, ResolvedEnvironment environment,
        string? operationId, WaitSettings settings, OutputFormat format, CancellationToken ct)
    {
        if (operationId is null)
            throw new PpacException(ExitCodes.General,
                $"The service gave no operation ID, so there is nothing to wait for. Look for it with: ppac op list {environment.Id}");

        var path = $"{EnvPath(environment.Id, "/operations/" + Uri.EscapeDataString(operationId))}?{ApiVersion}";

        var final = await OperationWaiter.WaitAsync(
            async t =>
            {
                var response = await client.SendAsync(HttpMethod.Get, path, null, t);
                return response.Json as JsonObject
                    ?? throw new PpacException(ExitCodes.General, "The service returned an empty response for the operation.");
            },
            settings.Interval, settings.Timeout,
            (wait, t) => Task.Delay(wait, t),
            () => DateTimeOffset.UtcNow,
            message => Console.Error.WriteLine(message),
            ct);

        ObjectWriter.Write(Console.Out, final, format);
    }
}