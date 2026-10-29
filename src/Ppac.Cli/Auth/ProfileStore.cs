using System.Text.Json;

namespace Ppac.Cli.Auth;

/// <summary>An auth profile. Contains no secrets; tokens live in the MSAL cache.</summary>
public sealed record Profile(
    string Name,
    string TenantId,
    string ClientId,
    string Cloud,
    string? Username,
    string? HomeAccountId);

public sealed class ProfileFile
{
    public string? Active { get; set; }
    public List<Profile> Profiles { get; set; } = new();
}

public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static ProfileFile Load()
    {
        var path = AppPaths.ProfilesFile;
        if (!File.Exists(path))
        {
            return new ProfileFile();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions) ?? new ProfileFile();
    }

    public static void Save(ProfileFile file)
    {
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(AppPaths.ProfilesFile, json);
    }

    /// <summary>Adds or replaces a profile by name (case-insensitive) and makes it active.</summary>
    public static void Upsert(Profile profile)
    {
        var file = Load();
        file.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        file.Profiles.Add(profile);
        file.Active = profile.Name;
        Save(file);
    }

    public static Profile? GetActive()
    {
        var file = Load();
        return file.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, file.Active, StringComparison.OrdinalIgnoreCase));
    }
}