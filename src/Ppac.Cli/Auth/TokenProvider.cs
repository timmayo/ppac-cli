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

    /// <summary>Signs in with the device code flow, printing the instructions MSAL provides.</summary>
    public static async Task<AuthenticationResult> SignInWithDeviceCodeAsync(
        IPublicClientApplication app, CloudConfig cloud, CancellationToken cancellationToken)
    {
        return await app
            .AcquireTokenWithDeviceCode(new[] { cloud.ApiScope }, deviceCode =>
            {
                Console.WriteLine(deviceCode.Message);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken);
    }
}