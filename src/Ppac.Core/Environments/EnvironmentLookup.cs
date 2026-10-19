using System.Text.Json.Nodes;
using Ppac.Core.Output;

namespace Ppac.Core.Environments;

public static class EnvironmentLookup
{
    /// <summary>
    /// Finds one environment by ID (preferred, exact) or by exact display name (case-insensitive).
    /// Not found exits 4. A display name shared by several environments is a usage error (exit 2)
    /// that lists the IDs, so scripts never act on the wrong environment.
    /// </summary>
    public static JsonObject Find(IReadOnlyList<JsonObject> environments, string idOrName)
    {
        var key = idOrName.Trim();

        var byId = environments.FirstOrDefault(e => Same(OutputWriter.CellText(e, "id"), key));
        if (byId is not null) return byId;

        var byName = environments.Where(e => Same(OutputWriter.CellText(e, "displayName"), key)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new PpacException(ExitCodes.Usage,
                $"'{key}' matches {byName.Count} environments. Use the environment ID instead: " +
                string.Join(", ", byName.Select(e => OutputWriter.CellText(e, "id"))));

        throw new PpacException(ExitCodes.NotFound, $"Environment '{key}' not found.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}