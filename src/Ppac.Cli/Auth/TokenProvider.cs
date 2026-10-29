using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Ppac.Cli.Config;

namespace Ppac.Cli.Auth;

public static class TokenProvider
{
    /// <summary>
    /// Builds a public client app for the tenant and client ID, with the
    /// token cache stored in a protected file (DPAPI on Windows).
    /// </summary>
    public static async Task<IPublicClientApplication> CreateAppAsync(
        string tenantId, string clientId, CloudConfig cloud)
    {
        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"{cloud.LoginAuthority}/{tenantId}")
            .WithDefaultRedirectUri()
            .Build();

        var storage = new StorageCreationPropertiesBuilder(
                AppPaths.TokenCacheFileName, AppPaths.Root)
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storage);
        cacheHelper.RegisterCache(app.UserTokenCache);

        return app;
    }

    /// <summary>
    /// Gets an access token for the profile from the cache, refreshing silently if needed.
    /// Throws MsalUiRequiredException when the user must sign in again.
    /// </summary>
    public static async Task<string> GetAccessTokenAsync(
        Profile profile, CloudConfig cloud, CancellationToken cancellationToken)
    {
        var app = await CreateAppAsync(profile.TenantId, profile.ClientId, cloud);

        IAccount? account = null;
        if (profile.HomeAccountId is not null)
        {
            account = await app.GetAccountAsync(profile.HomeAccountId);
        }
        if (account is null)
        {
            var accounts = await app.GetAccountsAsync();
            account = accounts.FirstOrDefault(a =>
                string.Equals(a.Username, profile.Username, StringComparison.OrdinalIgnoreCase));
        }
        if (account is null)
        {
            throw new MsalUiRequiredException("no_account", "No cached account for this profile.");
        }

        var result = await app
            .AcquireTokenSilent(new[] { cloud.ApiScope }, account)
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    /// <summary>Signs in with the device code flow, passing the sign-in instructions to showMessage.</summary>
    public static async Task<AuthenticationResult> SignInWithDeviceCodeAsync(
        IPublicClientApplication app,
        CloudConfig cloud,
        Action<string> showMessage,
        CancellationToken cancellationToken)
    {
        return await app
            .AcquireTokenWithDeviceCode(new[] { cloud.ApiScope }, deviceCode =>
            {
                showMessage(deviceCode.Message);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken);
    }
}