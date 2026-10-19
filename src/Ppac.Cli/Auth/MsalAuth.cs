using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Ppac.Core;
using Ppac.Core.Config;
using Ppac.Core.Http;

namespace Ppac.Cli.Auth;

/// <summary>
/// Signed-in admin authentication (delegated). Interactive browser or device code to sign in;
/// every other command only ever acquires tokens silently from the cache and never prompts.
/// </summary>
public sealed class MsalAuth : ITokenProvider
{
    private static readonly string[] Scopes = ["https://api.powerplatform.com/.default"];

    private readonly Profile _profile;
    private IPublicClientApplication? _app;

    public MsalAuth(Profile profile) => _profile = profile;

    public async Task<AuthenticationResult> LoginAsync(bool deviceCode, Action<string> showMessage, CancellationToken ct)
    {
        var app = await GetAppAsync();
        try
        {
            if (deviceCode)
            {
                return await app.AcquireTokenWithDeviceCode(Scopes, result =>
                {
                    showMessage(result.Message);
                    return Task.CompletedTask;
                }).ExecuteAsync(ct);
            }

            return await app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct);
        }
        catch (MsalException ex)
        {
            throw new PpacException(ExitCodes.Auth, $"Sign-in failed: {ex.Message}", ex.ToString(), ex);
        }
    }

    public async Task<AuthenticationResult> AcquireSilentAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var accounts = (await app.GetAccountsAsync()).ToList();
        var account = accounts.FirstOrDefault(a =>
                          string.Equals(a.Username, _profile.Username, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault();
        if (account is null) throw NotSignedIn();

        try
        {
            return await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            throw NotSignedIn();
        }
        catch (MsalException ex)
        {
            throw new PpacException(ExitCodes.Auth, $"Could not get a token: {ex.Message}", ex.ToString(), ex);
        }
    }

    public async Task<string> GetTokenAsync(CancellationToken ct) => (await AcquireSilentAsync(ct)).AccessToken;

    public async Task LogoutAsync()
    {
        var app = await GetAppAsync();
        foreach (var account in await app.GetAccountsAsync())
            await app.RemoveAsync(account);
    }

    private static PpacException NotSignedIn() =>
        new(ExitCodes.Auth, "Not signed in, or the session expired. Run: ppac auth login");

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;

        var app = PublicClientApplicationBuilder.Create(_profile.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{_profile.TenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

        // Encrypted with DPAPI on Windows and the Keychain on macOS. Linux uses a file readable only by the
        // current user, because a desktop keyring is usually not available on servers and in containers.
        var directory = ProfileStore.DefaultDirectory();
        Directory.CreateDirectory(directory);
        var storage = new StorageCreationPropertiesBuilder("msal.cache", directory);
        if (OperatingSystem.IsLinux()) storage = storage.WithLinuxUnprotectedFile();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storage.Build());
        cacheHelper.RegisterCache(app.UserTokenCache);

        _app = app;
        return app;
    }
}
