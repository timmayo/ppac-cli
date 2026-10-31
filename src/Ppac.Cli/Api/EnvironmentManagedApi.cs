using System.Text.Json.Nodes;
using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>
/// Managed Environments on/off for one environment (Environment Management API, api-version 2024-10-01,
/// "Environment Managed Governance" operations).
/// Enable:  POST /environmentmanagement/environments/{environmentId}/governanceSetting/enableManaged
/// Disable: POST /environmentmanagement/environments/{environmentId}/governanceSetting/disableManaged
/// The reference documents an optional ValidateOnly=true query, but the service answered 400
/// "ValidateOnly: ValidateOnly is not supported yet" for both operations (live 2026-10-30), so ppac does not
/// offer it. No request body is documented. Documented responses: 202 Accepted (no body,
/// no headers), 400 ValidationResponse, 401, 403, 429, and a "Conflict" response that also carries a
/// ValidationResponse. The reference pages list only the .default scope and do not name the permission or
/// role. The admin center documentation says the signed-in user needs the Power Platform Administrator or
/// Dynamics 365 Administrator role in Microsoft Entra ID.
///
/// How to tell whether an environment is managed: the environment object's protectionLevel. Live (2026-10-30) an
/// unmanaged environment returned "Basic" and a managed one "Standard"; the admin center PowerShell documentation
/// uses the same two values. Other values, if any exist, are treated as managed ("anything other than Basic").
/// Live, both calls answered 202 with an Operation-Location header (.../environments/{id}/operations/{opId}?api-version=1);
/// the reference documents no headers. The change showed up in about 5 seconds (enable) and 27 seconds (disable).
/// </summary>
public static class EnvironmentManagedApi
{
    public const string ApiVersion = "2024-10-01";

    public static PlannedRequest EnableRequest(CloudConfig cloud, string environmentId) =>
        Build(cloud, environmentId, "enableManaged");

    public static PlannedRequest DisableRequest(CloudConfig cloud, string environmentId) =>
        Build(cloud, environmentId, "disableManaged");

    private static PlannedRequest Build(CloudConfig cloud, string environmentId, string action) =>
        new(HttpMethod.Post,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environments/{Uri.EscapeDataString(environmentId)}/governanceSetting/{action}?api-version={ApiVersion}");

    /// <summary>How many back-to-back reads a decision is based on. See <see cref="SampleAsync"/>.</summary>
    public const int ReadSamples = 6;

    /// <summary>What repeated reads of one environment's protectionLevel said.</summary>
    public enum ReadState { Managed, NotManaged, Mixed, Unknown }

    /// <summary>
    /// Reads the environment's protectionLevel <see cref="ReadSamples"/> times back to back (the first read is passed
    /// in) and reports whether they agree. Live (2026-10-30, probe-read-consistency.ps1) the service answered
    /// 20 reads of one unchanging environment with Standard 18 times and Basic twice, and the group membership filter
    /// said "not a member" for a member 6 of 20 times; some answers were snapshots minutes old. A single read cannot
    /// justify "nothing to do". Mixed means the reads disagreed; Unknown means a read had no protectionLevel.
    /// </summary>
    public static async Task<ReadState> SampleAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, JsonNode? firstRead, CancellationToken cancellationToken)
    {
        var reads = new List<bool?> { IsManaged(firstRead) };
        for (var i = 1; i < ReadSamples; i++)
        {
            reads.Add(IsManaged(await EnvironmentApi.GetAsync(api, cloud, environmentId, "protectionLevel", cancellationToken)));
        }
        return Combine(reads);
    }

    public static ReadState Combine(IReadOnlyCollection<bool?> reads)
    {
        if (reads.Any(r => r is null))
        {
            return ReadState.Unknown;
        }
        if (reads.All(r => r == true))
        {
            return ReadState.Managed;
        }
        return reads.All(r => r == false) ? ReadState.NotManaged : ReadState.Mixed;
    }

    /// <summary>
    /// Is the environment managed? Null when the environment object has no protectionLevel, so the state
    /// cannot be read.
    /// </summary>
    public static bool? IsManaged(JsonNode? environment)
    {
        var level = environment?["protectionLevel"]?.ToString();
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }
        return !string.Equals(level, "Basic", StringComparison.OrdinalIgnoreCase);
    }
}
