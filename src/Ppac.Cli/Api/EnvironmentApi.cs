using System.Text.Json.Nodes;
using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>
/// Environment Management operations.
/// List: GET /environmentmanagement/environments?api-version=2024-10-01 (preview).
/// </summary>
public static class EnvironmentApi
{
    public const string ApiVersion = "2024-10-01";

    /// <summary>Lists environments, following @odata.nextlink until all pages (or --top) are read.</summary>
    public static async Task<JsonArray> ListAsync(
        PowerPlatformApi api,
        CloudConfig cloud,
        string? filter,
        int? top,
        CancellationToken cancellationToken)
    {
        var query = new List<string> { $"api-version={ApiVersion}" };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query.Add("$filter=" + Uri.EscapeDataString(filter));
        }
        if (top is not null)
        {
            query.Add("$top=" + top.Value);
        }

        string? url = $"{cloud.ApiBaseUrl}/environmentmanagement/environments?{string.Join("&", query)}";
        var all = new JsonArray();

        while (url is not null)
        {
            var page = await api.GetJsonAsync(url, cancellationToken);

            if (page["value"] is JsonArray items)
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

            // The reference page spells it "@odata.nextlink"; accept the usual OData casing too.
            url = (page["@odata.nextlink"] ?? page["@odata.nextLink"])?.ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                url = null;
            }
        }

        return all;
    }
}