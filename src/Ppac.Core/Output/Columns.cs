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
}