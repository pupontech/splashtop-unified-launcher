using System.IO;
using System.Text.Json;

namespace SplashtopUnified.AccountBrowser;

internal sealed record AccountProfile(string Id, string Name, string ConsoleUrl);

internal sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AccountStore()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            throw new InvalidOperationException("Windows did not provide a per-user LocalAppData directory; account profiles were not placed in a relative or shared folder.");
        }

        RootDirectory = Path.Combine(local, "SplashtopUnified", "AccountBrowser");
        FilePath = Path.Combine(RootDirectory, "accounts.json");
        ProfilesDirectory = Path.Combine(RootDirectory, "Profiles");
    }

    public string RootDirectory { get; }
    public string FilePath { get; }
    public string ProfilesDirectory { get; }

    public List<AccountProfile> LoadOrCreate()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        if (!File.Exists(FilePath))
        {
            var initial = new List<AccountProfile>
            {
                new(Guid.NewGuid().ToString("N"), "Global account", "https://my.splashtop.com/"),
                new(Guid.NewGuid().ToString("N"), "EU account", "https://my.splashtop.eu/")
            };
            Save(initial);
            return initial;
        }

        var json = File.ReadAllText(FilePath);
        var profiles = JsonSerializer.Deserialize<List<AccountProfile>>(json, JsonOptions)
            ?? throw new InvalidDataException("The saved account list is empty or unreadable.");
        ValidateProfiles(profiles);
        return profiles;
    }

    public void Save(IReadOnlyCollection<AccountProfile> profiles)
    {
        ValidateProfiles(profiles);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profiles, JsonOptions));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    public string GetUserDataFolder(AccountProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _))
        {
            throw new InvalidDataException("An account profile has an invalid opaque ID.");
        }

        var path = Path.Combine(ProfilesDirectory, profile.Id);
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool TryNormalizeConsoleUrl(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = "Use the official HTTPS console at my.splashtop.com or my.splashtop.eu.";
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(string.Equals(uri.IdnHost, "my.splashtop.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.IdnHost, "my.splashtop.eu", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = string.Empty;
        return true;
    }

    private static void ValidateProfiles(IReadOnlyCollection<AccountProfile> profiles)
    {
        if (profiles.Count < 2)
        {
            throw new InvalidDataException("At least two account profiles are required for split view.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is null || !Guid.TryParseExact(profile.Id, "N", out _) || !ids.Add(profile.Id))
            {
                throw new InvalidDataException("Account profiles must have unique opaque identifiers.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
            {
                throw new InvalidDataException("Each account needs a display name of 1–80 characters.");
            }

            if (!TryNormalizeConsoleUrl(profile.ConsoleUrl, out _, out var error))
            {
                throw new InvalidDataException($"Account '{profile.Name}' has an invalid console URL. {error}");
            }
        }
    }
}
