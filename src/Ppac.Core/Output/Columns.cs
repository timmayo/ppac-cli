namespace Ppac.Core.Output;

/// <summary>Table columns per resource. Paths match the API response (live output where noted in the tests).</summary>
public static class Columns
{
    public static readonly IReadOnlyList<Column> Environments =
    [
        new("DisplayName", "displayName"),
        new("Type", "type"),
        new("State", "state"),
        new("Region", "azureRegion"),
        new("Id", "id"),
    ];

    public static readonly IReadOnlyList<Column> Groups =
    [
        new("DisplayName", "displayName"),
        new("Description", "description"),
        new("Created", "createdTime"),
        new("Id", "id"),
    ];

    /// <summary>From Microsoft's reference for EnvironmentBackup; not yet checked against live output.</summary>
    public static readonly IReadOnlyList<Column> Backups =
    [
        new("Label", "label"),
        new("BackupPoint", "backupPointDateTime"),
        new("Expires", "backupExpiryDateTime"),
        new("Id", "id"),
    ];

    /// <summary>From Microsoft's reference for OperationExecutionResult; not yet checked against live output.</summary>
    public static readonly IReadOnlyList<Column> Operations =
    [
        new("Name", "name"),
        new("Status", "status"),
        new("Started", "startTime"),
        new("Ended", "endTime"),
        new("OperationId", "operationId"),
    ];

    /// <summary>Columns of the flattened inventory rows built by InventoryQuery.Flatten.</summary>
    public static readonly IReadOnlyList<Column> Inventory =
    [
        new("Name", "displayName"),
        new("Type", "type"),
        new("Owner", "ownerId"),
        new("Modified", "lastModifiedAt"),
        new("Environment", "environmentName"),
        new("EnvType", "environmentType"),
        new("Status", "status"),
        new("Connectors", "connectors"),
        new("Id", "id"),
    ];

    /// <summary>Two-column view used when a single object is shown as a table, CSV or TSV.</summary>
    public static readonly IReadOnlyList<Column> Properties =
    [
        new("Property", "property"),
        new("Value", "value"),
    ];
}