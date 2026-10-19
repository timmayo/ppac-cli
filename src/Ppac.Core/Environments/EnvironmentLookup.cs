using System.Text.Json.Nodes;

namespace Ppac.Core.Environments;

public static class EnvironmentLookup
{
    /// <summary>Finds one environment by ID or exact display name. See <see cref="ResourceLookup.Find"/>.</summary>
    public static JsonObject Find(IReadOnlyList<JsonObject> environments, string idOrName) =>
        ResourceLookup.Find(environments, idOrName, "Environment");
}