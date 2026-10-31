using System.Text.Json.Nodes;
using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>
/// Environment group operations (Environment Management API, api-version 2024-10-01, not labeled preview).
/// List: GET /environmentmanagement/environmentGroups  (tenant level; paging via @odata.nextLink)
/// Get:  GET /environmentmanagement/environmentGroups/{groupId}
/// Both reference pages list only the .default scope; the permission reference names
/// EnvironmentManagement.Groups.Read ("Read environment groups").
/// A 204 No Content is a documented response for both calls.
/// </summary>
public static class EnvironmentGroupApi
{
    public const string ApiVersion = "2024-10-01";

    public static PlannedRequest ListRequest(CloudConfig cloud) =>
        new(HttpMethod.Get, $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups?api-version={ApiVersion}");

    public static PlannedRequest GetRequest(CloudConfig cloud, string groupId) =>
        new(HttpMethod.Get,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups/{Uri.EscapeDataString(groupId)}?api-version={ApiVersion}");

    /// <summary>
    /// Lists groups, following @odata.nextLink until all pages (or top) are read.
    /// No $top is documented, so top is applied here and paging stops once enough are read.
    /// A 204 (no body) is an empty list.
    /// </summary>
    public static async Task<JsonArray> ListAsync(
        PowerPlatformApi api, CloudConfig cloud, int? top, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        string? url = ListRequest(cloud).Url;

        while (url is not null)
        {
            var page = await api.SendAsync(new PlannedRequest(HttpMethod.Get, url), cancellationToken);

            if (page?["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    all.Add(item?.DeepClone());
                }
            }

            if (top is not null && all.Count >= top.Value)
            {
                while (all.Count > top.Value)
                {
                    all.RemoveAt(all.Count - 1);
                }
                break;
            }

            var next = (page?["@odata.nextLink"] ?? page?["@odata.nextlink"])?.ToString();
            url = string.IsNullOrWhiteSpace(next) || next == url ? null : next;
        }

        return all;
    }

    /// <summary>Gets one group. Returns null for a 204 No Content. Throws ApiException (for example 404).</summary>
    public static Task<JsonNode?> GetAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, CancellationToken cancellationToken) =>
        api.SendAsync(GetRequest(cloud, groupId), cancellationToken);
}
