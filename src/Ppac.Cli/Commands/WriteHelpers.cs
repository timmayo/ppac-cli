using System.CommandLine;
using System.Net;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Environments;
using Ppac.Core.Http;
using Ppac.Core.Output;
using Ppac.Core.Safety;

namespace Ppac.Cli.Commands;

public sealed record ResolvedEnvironment(string Id, string Name);

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

    public static Confirmation CreateConfirmation() =>
        new(Console.In, Console.Error, interactive: !Console.IsInputRedirected);

    public static string EnvPath(string environmentId, string suffix = "") =>
        $"{EnvironmentsPath}/{Uri.EscapeDataString(environmentId)}{suffix}";

    public static async Task<ResolvedEnvironment> ResolveEnvironmentAsync(PpApiClient client, string idOrName, CancellationToken ct)
    {
        var rows = await client.GetAllAsync($"{EnvironmentsPath}?{ApiVersion}", ct);
        var found = EnvironmentLookup.Find(rows, idOrName);
        return new ResolvedEnvironment(OutputWriter.CellText(found, "id"), OutputWriter.CellText(found, "displayName"));
    }

    /// <summary>
    /// Reports the result of a validation-only request. A 202 is flagged because it is not documented whether
    /// the service answers a validation-only request with 200 or 202, and 202 could mean it started the work.
    /// </summary>
    public static void ReportValidation(ApiResponse response, string wouldDo, string checkHint)
    {
        if (response.Status == HttpStatusCode.Accepted)
            Console.Error.WriteLine(
                "WARNING: the service answered 202 Accepted to a validation-only request. It is not documented whether " +
                $"that means validation only, so check whether anything started with: {checkHint}");
        else
            Console.Error.WriteLine($"Validation passed. Nothing was changed. Would {wouldDo}.");
    }

    /// <summary>Prints what the service returned for an accepted (202) request, including the headers it sent.</summary>
    public static void WriteAccepted(ParseResult parse, string action, ResolvedEnvironment environment,
        ApiResponse response, OutputFormat format)
    {
        var row = new JsonObject
        {
            ["action"] = action,
            ["environmentId"] = environment.Id,
            ["environmentName"] = environment.Name,
            ["httpStatus"] = (int)response.Status,
        };

        if (response.Headers.TryGetValue("Operation-Location", out var operationLocation))
            row["operationLocation"] = operationLocation;
        if (response.Headers.TryGetValue("Location", out var location))
            row["location"] = location;

        if (response.Json is not null) row["response"] = response.Json.DeepClone();
        else if (!string.IsNullOrWhiteSpace(response.Body)) row["responseBody"] = response.Body;

        ObjectWriter.Write(Console.Out, row, format);

        if (parse.GetValue(GlobalOptions.Verbose))
            foreach (var header in response.Headers)
                Console.Error.WriteLine($"{header.Key}: {header.Value}");

        Console.Error.WriteLine($"Accepted. Track progress with: ppac op list {environment.Id}");
    }
}