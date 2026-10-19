using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ppac.Core.Http;
using Ppac.Core.Output;

namespace Ppac.Core.Inventory;

public sealed record EnvironmentInfo(string Name, string Type);

public sealed record InventoryResult(List<JsonObject> Rows, long? TotalRecords);

/// <summary>
/// Power Platform inventory: POST /resourcequery/resources/query (api-version 2024-10-01), a structured query that
/// the service runs against Azure Resource Graph. Delegated sign-in only (service principals get 403).
/// Request shape from Microsoft's inventory API page; row shape checked against live output.
/// </summary>
public static class InventoryQuery
{
    public const string Path = "/resourcequery/resources/query?api-version=2024-10-01";
    public const string Table = "PowerPlatformResources";
    public const int MaxPageSize = 1000;

    private static readonly string[] Apps =
    [
        "microsoft.powerapps/canvasapps", "microsoft.powerapps/modeldrivenapps",
        "microsoft.powerapps/codeapps", "microsoft.powerapps/apps",
    ];

    private static readonly string[] Flows =
    [
        "microsoft.powerautomate/cloudflows", "microsoft.powerautomate/agentflows",
        "microsoft.powerautomate/m365agentflows",
    ];

    private static readonly string[] Agents = ["microsoft.copilotstudio/agents"];

    private static readonly Dictionary<string, string[]> TypeGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = Apps,
        ["canvas-app"] = ["microsoft.powerapps/canvasapps"],
        ["model-driven-app"] = ["microsoft.powerapps/modeldrivenapps"],
        ["code-app"] = ["microsoft.powerapps/codeapps"],
        ["flow"] = Flows,
        ["cloud-flow"] = ["microsoft.powerautomate/cloudflows"],
        ["agent-flow"] = ["microsoft.powerautomate/agentflows"],
        ["agent"] = Agents,
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft.powerapps/canvasapps"] = "Canvas app",
        ["microsoft.powerapps/modeldrivenapps"] = "Model-driven app",
        ["microsoft.powerapps/codeapps"] = "Code app",
        ["microsoft.powerapps/apps"] = "App",
        ["microsoft.powerautomate/cloudflows"] = "Cloud flow",
        ["microsoft.powerautomate/agentflows"] = "Agent flow",
        ["microsoft.powerautomate/m365agentflows"] = "Workflow agent flow",
        ["microsoft.copilotstudio/agents"] = "Copilot agent",
    };

    private static readonly Regex SafeId = new("^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> TypeNames => TypeGroups.Keys.ToList();

    /// <summary>The resource types for a --type value. No value means apps, flows and agents.</summary>
    public static IReadOnlyList<string> ResolveTypes(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [.. Apps, .. Flows, .. Agents];
        if (TypeGroups.TryGetValue(name.Trim(), out var types)) return types;
        throw new PpacException(ExitCodes.Usage,
            $"Unknown type '{name.Trim()}'. Use one of: {string.Join(", ", TypeNames)}.");
    }

    public static string Label(string resourceType) =>
        Labels.TryGetValue(resourceType, out var label) ? label : resourceType;

    /// <summary>Request body. Values are validated because they are embedded in single quotes in the query.</summary>
    public static JsonObject BuildRequest(IReadOnlyList<string> types, string? environmentId, string? ownerId,
        int pageSize, string? skipToken)
    {
        var clauses = new JsonArray { In("type", types) };
        if (!string.IsNullOrWhiteSpace(environmentId))
            clauses.Add(In("properties.environmentId", [Safe(environmentId, "environment ID").ToLowerInvariant()]));
        if (!string.IsNullOrWhiteSpace(ownerId))
            clauses.Add(In("properties.ownerId", [Safe(ownerId, "owner ID").ToLowerInvariant()]));

        clauses.Add(new JsonObject
        {
            ["$type"] = "orderby",
            ["FieldNamesAscDesc"] = new JsonObject { ["tostring(properties.lastModifiedAt)"] = "desc" },
        });

        // Live: sending Skip = 0 together with a SkipToken made the service return page 1 again, every time.
        // So Skip is never sent, and SkipToken only when continuing.
        var options = new JsonObject { ["Top"] = pageSize };
        if (!string.IsNullOrEmpty(skipToken)) options["SkipToken"] = skipToken;

        return new JsonObject
        {
            ["TableName"] = Table,
            ["Options"] = options,
            ["Clauses"] = clauses,
        };
    }

    /// <summary>
    /// Runs the query and follows skipToken until the rows run out or the limit is reached.
    /// A null limit means every row. resultTruncated is ignored: live responses showed 1 both on a partial page
    /// with a skipToken and on a complete one, so only skipToken decides whether there is more.
    /// A repeated token would loop forever, so it stops with an error instead.
    /// </summary>
    public static async Task<InventoryResult> QueryAsync(PpApiClient client, IReadOnlyList<string> types,
        string? environmentId, string? ownerId, int? limit, int pageSize, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        long? total = null;
        string? token = null;

        for (var page = 0; page < 10000; page++)
        {
            var top = limit is null ? pageSize : Math.Min(pageSize, limit.Value - rows.Count);
            var body = BuildRequest(types, environmentId, ownerId, top, token);
            var response = await client.SendAsync(HttpMethod.Post, Path, body.ToJsonString(), ct, readOnly: true)
                .ConfigureAwait(false);

            var obj = response.Json as JsonObject
                ?? throw new PpacException(ExitCodes.General, "The service returned an empty response for the inventory query.");

            if (total is null && obj["totalRecords"] is JsonValue t && t.TryGetValue<long>(out var count)) total = count;

            if (obj["data"] is JsonArray data)
                foreach (var node in data)
                    if (node is JsonObject row)
                        rows.Add(row);

            var previous = token;
            token = obj["skipToken"] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
                ? text
                : null;
            if (token is not null && token == previous)
                throw new PpacException(ExitCodes.General,
                    "The service returned the same page token twice, so paging cannot continue. Try a smaller --limit.");

            if (limit is not null && rows.Count >= limit.Value) return new InventoryResult(rows.Take(limit.Value).ToList(), total);
            if (token is null) return new InventoryResult(rows, total);
        }

        throw new PpacException(ExitCodes.General, "Paging did not finish after 10000 pages.");
    }

    /// <summary>
    /// One flat row per item. The environment name and type are looked up by environment ID from the
    /// environment list, because the owner and environment are IDs in the inventory data.
    /// </summary>
    public static JsonObject Flatten(JsonObject row, IReadOnlyDictionary<string, EnvironmentInfo> environments)
    {
        var props = row["properties"] as JsonObject;
        string P(string key) => props is null ? "" : OutputWriter.CellText(props, key);

        var resourceType = OutputWriter.CellText(row, "type");
        var environmentId = P("environmentId");
        environments.TryGetValue(environmentId.ToLowerInvariant(), out var env);

        var connectors = new SortedSet<string>(StringComparer.Ordinal);
        if (props?["powerPlatformConnectors"] is JsonArray list)
            foreach (var item in list)
                if (item is JsonObject c && OutputWriter.CellText(c, "connectorId") is { Length: > 0 } id)
                    connectors.Add(id);

        return new JsonObject
        {
            ["id"] = OutputWriter.CellText(row, "name"),
            ["displayName"] = P("displayName"),
            ["type"] = Label(resourceType),
            ["resourceType"] = resourceType,
            ["environmentId"] = environmentId,
            ["environmentName"] = env?.Name ?? "",
            ["environmentType"] = env?.Type ?? "",
            ["ownerId"] = P("ownerId"),
            ["createdAt"] = P("createdAt"),
            ["lastModifiedAt"] = P("lastModifiedAt"),
            ["status"] = P("status"),
            ["connectors"] = string.Join(",", connectors),
        };
    }

    private static JsonObject In(string field, IReadOnlyList<string> values) => new()
    {
        ["$type"] = "where",
        ["FieldName"] = field,
        ["Operator"] = "in~",
        ["Values"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create($"'{v}'")!).ToArray()),
    };

    private static string Safe(string value, string what)
    {
        var text = value.Trim();
        if (!SafeId.IsMatch(text))
            throw new PpacException(ExitCodes.Usage, $"'{text}' is not a valid {what}.");
        return text;
    }
}