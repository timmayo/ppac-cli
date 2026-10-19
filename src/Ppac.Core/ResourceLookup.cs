using System.Text.Json.Nodes;
using Ppac.Core.Output;

namespace Ppac.Core;

public static class ResourceLookup
{
    /// <summary>
    /// Finds one resource by ID (preferred, exact) or by exact display name (case-insensitive).
    /// Not found exits 4. A display name shared by several resources is a usage error (exit 2)
    /// that lists the IDs, so scripts never act on the wrong resource.
    /// </summary>
    public static JsonObject Find(IReadOnlyList<JsonObject> rows, string idOrName, string noun)
    {
        var key = idOrName.Trim();

        var byId = rows.FirstOrDefault(r => Same(OutputWriter.CellText(r, "id"), key));
        if (byId is not null) return byId;

        var byName = rows.Where(r => Same(OutputWriter.CellText(r, "displayName"), key)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new PpacException(ExitCodes.Usage,
                $"'{key}' matches {byName.Count} {noun.ToLowerInvariant()}s. Use the ID instead: " +
                string.Join(", ", byName.Select(r => OutputWriter.CellText(r, "id"))));

        throw new PpacException(ExitCodes.NotFound, $"{noun} '{key}' not found.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}