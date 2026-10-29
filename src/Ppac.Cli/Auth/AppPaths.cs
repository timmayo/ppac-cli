namespace Ppac.Cli.Auth;

/// <summary>
/// Where ppac keeps its local files. Set PPAC_HOME to override (useful for tests).
/// </summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("PPAC_HOME");
            var root = !string.IsNullOrWhiteSpace(overridePath)
                ? overridePath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ppac");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string ProfilesFile => Path.Combine(Root, "profiles.json");

    public const string TokenCacheFileName = "msal.cache";
}