namespace Ppac.Core.Output;

/// <summary>Table columns per resource. Paths match the real API response (checked against live output).</summary>
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

    /// <summary>Two-column view used when a single object is shown as a table, CSV or TSV.</summary>
    public static readonly IReadOnlyList<Column> Properties =
    [
        new("Property", "property"),
        new("Value", "value"),
    ];
}