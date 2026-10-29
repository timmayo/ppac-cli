namespace Ppac.Cli.Config;

/// <summary>
/// Endpoints for one cloud. Only the commercial cloud is defined for now;
/// other clouds can be added here without changing the commands.
/// </summary>
public sealed record CloudConfig(
    string Name,
    string LoginAuthority,
    string ApiBaseUrl,
    string ApiScope)
{
    public static readonly CloudConfig Commercial = new(
        Name: "Commercial",
        LoginAuthority: "https://login.microsoftonline.com",
        ApiBaseUrl: "https://api.powerplatform.com",
        ApiScope: "https://api.powerplatform.com/.default");

    public static CloudConfig Get(string name) =>
        string.Equals(name, Commercial.Name, StringComparison.OrdinalIgnoreCase)
            ? Commercial
            : throw new InvalidOperationException($"Unknown cloud '{name}'.");
}