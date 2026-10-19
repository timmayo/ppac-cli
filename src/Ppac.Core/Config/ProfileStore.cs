using System.Text.Json;

namespace Ppac.Core.Config;

/// <summary>How a profile signs in. Only Interactive is implemented so far.</summary>
public static class ProfileMethods
{
    public const string Interactive = "interactive";
}

/// <summary>
/// Non-secret sign-in settings for one named profile. Tokens live in the MSAL cache and secrets are never stored here.
/// </summary>
public sealed record Profile(
    string TenantId,
    string ClientId,
    string? Username = null,
    string Method = ProfileMethods.Interactive,
    string? CertificatePath = null,
    string? Name = null);

public sealed class ProfilesFile
{
    public string? Active { get; set; }
    public List<Profile> Profiles { get; set; } = [];
}

/// <summary>
/// Named sign-in profiles, modelled on "pac auth": several profiles, one active, selected by 1-based index or name.
/// Stored in profiles.json. A profile.json from before profiles existed is read as a profile named "default".
/// </summary>
public sealed class ProfileStore
{
    public const string DefaultName = "default";
    public const int MaxNameLength = 30;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly string _legacyPath;

    public string Directory { get; }

    public ProfileStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory();
        _path = Path.Combine(Directory, "profiles.json");
        _legacyPath = Path.Combine(Directory, "profile.json");
    }

    /// <summary>%APPDATA%\ppac on Windows; $XDG_CONFIG_HOME/ppac or ~/.config/ppac elsewhere.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ppac");

    public ProfilesFile LoadAll()
    {
        if (File.Exists(_path)) return Read<ProfilesFile>(_path) ?? new ProfilesFile();

        if (File.Exists(_legacyPath) && Read<Profile>(_legacyPath) is { } legacy)
            return new ProfilesFile { Active = DefaultName, Profiles = [legacy with { Name = DefaultName }] };

        return new ProfilesFile();
    }

    public void SaveAll(ProfilesFile file)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Returns the trimmed name, or throws a usage error when it is empty or longer than 30 characters.</summary>
    public static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length < 1 || trimmed.Length > MaxNameLength)
            throw new PpacException(ExitCodes.Usage, $"A profile name must be 1 to {MaxNameLength} characters.");
        return trimmed;
    }

    /// <summary>Adds the profile, or replaces the one with the same name (case-insensitive).</summary>
    public void Upsert(Profile profile, bool makeActive)
    {
        var name = ValidateName(profile.Name);
        var entry = profile with { Name = name };

        var file = LoadAll();
        var index = IndexOf(file, name);
        if (index >= 0) file.Profiles[index] = entry;
        else file.Profiles.Add(entry);

        if (makeActive) file.Active = name;
        SaveAll(file);
    }

    /// <summary>Finds a profile by 1-based index or by name. Exactly one of the two must be given.</summary>
    public static Profile Find(ProfilesFile file, string? name, int index)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasIndex = index > 0;
        if (hasName == hasIndex)
            throw new PpacException(ExitCodes.Usage, "Specify either --index or --name. See: ppac auth list");

        if (hasIndex)
        {
            if (index > file.Profiles.Count)
                throw new PpacException(ExitCodes.NotFound, $"There is no profile at index {index}. See: ppac auth list");
            return file.Profiles[index - 1];
        }

        var at = IndexOf(file, name!.Trim());
        if (at < 0)
            throw new PpacException(ExitCodes.NotFound, $"Profile '{name!.Trim()}' not found. See: ppac auth list");
        return file.Profiles[at];
    }

    public void Select(string? name, int index)
    {
        var file = LoadAll();
        file.Active = Find(file, name, index).Name;
        SaveAll(file);
    }

    /// <summary>Deletes a profile. If it was the active one, no profile is active afterwards.</summary>
    public Profile Delete(string? name, int index)
    {
        var file = LoadAll();
        var found = Find(file, name, index);
        file.Profiles.RemoveAll(p => string.Equals(p.Name, found.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(file.Active, found.Name, StringComparison.OrdinalIgnoreCase)) file.Active = null;
        SaveAll(file);
        return found;
    }

    public void Rename(int index, string newName)
    {
        var name = ValidateName(newName);
        var file = LoadAll();
        var found = Find(file, null, index);

        var clash = IndexOf(file, name);
        if (clash >= 0 && !string.Equals(file.Profiles[clash].Name, found.Name, StringComparison.OrdinalIgnoreCase))
            throw new PpacException(ExitCodes.Conflict, $"A profile named '{name}' already exists.");

        var at = index - 1;
        var wasActive = string.Equals(file.Active, found.Name, StringComparison.OrdinalIgnoreCase);
        file.Profiles[at] = found with { Name = name };
        if (wasActive) file.Active = name;
        SaveAll(file);
    }

    public void Clear() => SaveAll(new ProfilesFile());

    /// <summary>
    /// The profile that also uses the same signed-in account (same user and tenant), if any. Profiles share one
    /// token cache, so the cached sign-in must be kept while another profile still uses it.
    /// </summary>
    public static Profile? OtherProfileUsingAccount(ProfilesFile file, Profile profile)
    {
        if (string.IsNullOrEmpty(profile.Username)) return null;
        return file.Profiles.FirstOrDefault(p =>
            !string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Username, profile.Username, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A named profile (throws if it does not exist), or the active one, or null when none is active.</summary>
    public Profile? TryGet(string? profileName)
    {
        var file = LoadAll();
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            var at = IndexOf(file, profileName.Trim());
            if (at < 0)
                throw new PpacException(ExitCodes.NotFound, $"Profile '{profileName.Trim()}' not found. See: ppac auth list");
            return file.Profiles[at];
        }

        if (file.Active is null) return null;
        var active = IndexOf(file, file.Active);
        return active >= 0 ? file.Profiles[active] : null;
    }

    /// <summary>Kept for callers that handle a single profile: the active profile.</summary>
    public Profile? Load() => TryGet(null);

    /// <summary>Kept for callers that handle a single profile: saves it as the active profile (named "default" if unnamed).</summary>
    public void Save(Profile profile) => Upsert(profile with { Name = profile.Name ?? DefaultName }, makeActive: true);

    /// <summary>
    /// Settings for a command. Profile: --profile, then PPAC_PROFILE, then the active profile.
    /// Tenant and client: command-line flag, then PPAC_TENANT_ID / PPAC_CLIENT_ID, then the profile.
    /// </summary>
    public Profile Resolve(string? tenantFlag, string? clientFlag, Func<string, string?> getEnv, string? profileName = null)
    {
        var saved = TryGet(First(profileName, getEnv("PPAC_PROFILE")));
        var tenant = First(tenantFlag, getEnv("PPAC_TENANT_ID"), saved?.TenantId);
        var client = First(clientFlag, getEnv("PPAC_CLIENT_ID"), saved?.ClientId);

        if (tenant is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing tenant ID. Pass --tenant, set PPAC_TENANT_ID, or run: ppac auth create --tenant <id> --client-id <id>");
        if (client is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing client ID. Pass --client-id, set PPAC_CLIENT_ID, or run: ppac auth create --tenant <id> --client-id <id>");

        return new Profile(tenant, client, saved?.Username, saved?.Method ?? ProfileMethods.Interactive,
            saved?.CertificatePath, saved?.Name);
    }

    /// <summary>
    /// Settings for creating or refreshing the profile called name. Tenant and client come from the flags, then
    /// the environment, then the existing profile of that name. They are not copied from other profiles.
    /// </summary>
    public Profile ResolveForCreate(string? name, string? tenantFlag, string? clientFlag, Func<string, string?> getEnv)
    {
        var validName = ValidateName(name ?? DefaultName);
        var file = LoadAll();
        var at = IndexOf(file, validName);
        var existing = at >= 0 ? file.Profiles[at] : null;

        var tenant = First(tenantFlag, getEnv("PPAC_TENANT_ID"), existing?.TenantId);
        var client = First(clientFlag, getEnv("PPAC_CLIENT_ID"), existing?.ClientId);

        if (tenant is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing tenant ID. Pass --tenant or set PPAC_TENANT_ID. Example: ppac auth create --tenant <id> --client-id <id>");
        if (client is null)
            throw new PpacException(ExitCodes.Usage,
                "Missing client ID. Pass --client-id or set PPAC_CLIENT_ID. Example: ppac auth create --tenant <id> --client-id <id>");

        return new Profile(tenant, client, null, ProfileMethods.Interactive, null, validName);
    }

    private T? Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            throw new PpacException(ExitCodes.General, $"Could not read {path}. Delete it and run: ppac auth create");
        }
    }

    private static int IndexOf(ProfilesFile file, string name) =>
        file.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}