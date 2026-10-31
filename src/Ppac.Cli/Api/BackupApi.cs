using System.Text.Json.Nodes;
using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>One page of backups. ContinuationToken is set when the service says more exist.</summary>
public sealed record BackupPage(JsonArray Items, string? ContinuationToken);

/// <summary>
/// Result of looking for one backup in the list. Backup is null when it was not found;
/// Complete is false when the service returned a continuation token, in which case
/// "not found" is not conclusive (paging is not documented).
/// </summary>
public sealed record BackupLookup(JsonNode? Backup, bool Complete)
{
    public bool Found => Backup is not null;
    public bool NotFound => Backup is null && Complete;
}

/// <summary>
/// Environment backup operations (Environment Management API, api-version 2024-10-01).
/// List:   GET    /environmentmanagement/environments/{environmentId}/backups
/// Create: POST   /environmentmanagement/environments/{environmentId}/backups   body {"label": "..."}
/// Delete: DELETE /environmentmanagement/environments/{environmentId}/backups/{backupId}
/// No permission is documented for these calls (see the status notes); unverified until run live.
/// </summary>
public static class BackupApi
{
    public const string ApiVersion = "2024-10-01";

    public static PlannedRequest ListRequest(CloudConfig cloud, string environmentId) =>
        new(HttpMethod.Get, $"{BackupsUrl(cloud, environmentId)}?api-version={ApiVersion}");

    public static PlannedRequest CreateRequest(CloudConfig cloud, string environmentId, string label) =>
        new(HttpMethod.Post,
            $"{BackupsUrl(cloud, environmentId)}?api-version={ApiVersion}",
            new JsonObject { ["label"] = label });

    public static PlannedRequest DeleteRequest(CloudConfig cloud, string environmentId, string backupId) =>
        new(HttpMethod.Delete,
            $"{BackupsUrl(cloud, environmentId)}/{Uri.EscapeDataString(backupId)}?api-version={ApiVersion}");

    /// <summary>
    /// Reads the first page of backups. The list reference documents no paging or filter
    /// parameters, so a continuation token is reported but cannot be followed.
    /// </summary>
    public static async Task<BackupPage> ListAsync(
        PowerPlatformApi api, CloudConfig cloud, string environmentId, CancellationToken cancellationToken)
    {
        var page = await api.GetJsonAsync(ListRequest(cloud, environmentId).Url, cancellationToken);

        var items = new JsonArray();
        if (page["collection"] is JsonArray collection)
        {
            foreach (var item in collection)
            {
                items.Add(item?.DeepClone());
            }
        }

        var token = page["continuationToken"]?.ToString();
        return new BackupPage(items, string.IsNullOrWhiteSpace(token) ? null : token);
    }

    /// <summary>Finds one backup by ID in the list (there is no documented get-one call).</summary>
    public static async Task<BackupLookup> FindAsync(
        PowerPlatformApi api,
        CloudConfig cloud,
        string environmentId,
        string backupId,
        CancellationToken cancellationToken)
    {
        var page = await ListAsync(api, cloud, environmentId, cancellationToken);

        var match = page.Items.FirstOrDefault(b =>
            string.Equals(b?["id"]?.ToString(), backupId, StringComparison.OrdinalIgnoreCase));

        return new BackupLookup(match?.DeepClone(), Complete: page.ContinuationToken is null);
    }

    private static string BackupsUrl(CloudConfig cloud, string environmentId) =>
        $"{cloud.ApiBaseUrl}/environmentmanagement/environments/{Uri.EscapeDataString(environmentId)}/backups";
}
