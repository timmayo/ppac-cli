using System.Text.Json;

namespace Ppac.Core.Config;

/// <summary>Non-secret sign-in settings. Tokens live in the MSAL cache, never here.</summary>
public sealed record Profile(string TenantId, string ClientId, string? Username = null);

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public string Directory { get; }

    public ProfileStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory();
        _path = Path.Combine(Directory, "profile.json");
    }

    /// <summary>%APPDATA%\ppac on Windows; $XDG_CONFIG_HOME/ppac or ~/.config/ppac elsewhere.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ppac");

    public Profile? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Profile>(File.ReadAllText(_path), Options);
        }
        catch (JsonException)
        {
            throw new PpacException(ExitCodes.General, $"Could not read {_path}. Delete it and run: ppac auth login");
        }
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(profile, Options));
    }

    /// <summary>Precedence: command-line flag, then PPAC_TENANT_ID / PPAC_CLIENT_ID, then the saved profile.</summary>
    public Profile Resolve(string? tenantFlag, string? clientFlag, Func<string, string?> getEnv)
    {
        var saved = Load();
        var tenant = First(tenantFlag, getEnv("PPAC_TENANT_ID"), saved?.TenantId);
        var client = First(clientFlag, getEnv("PPAC_CLIENT_ID"), saved?.ClientId);

        if (tenant is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing tenant ID. Pass --tenant, set PPAC_TENANT_ID, or run: ppac auth login --tenant <id> --client-id <id>");
        if (client is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing client ID. Pass --client-id, set PPAC_CLIENT_ID, or run: ppac auth login --tenant <id> --client-id <id>");

        return new Profile(tenant, client, saved?.Username);
    }

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
