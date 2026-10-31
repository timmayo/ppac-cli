using System.Net;
using Microsoft.Identity.Client;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

/// <summary>
/// Signs in from the active profile, runs an API call, and maps failures to exit codes.
/// Shared by every command that calls the Power Platform API.
/// </summary>
public static class ApiCommandRunner
{
    /// <param name="call">The work to do once signed in.</param>
    /// <param name="forbiddenHint">Printed after a 401/403: what to check (permission, role).</param>
    /// <param name="notFoundHint">Printed after a 404: what to check.</param>
    public static async Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call,
        string forbiddenHint,
        string notFoundHint,
        CancellationToken cancellationToken)
    {
        var profile = ProfileStore.GetActive();
        if (profile is null)
        {
            Console.Error.WriteLine("No active profile. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.");
            return ExitCodes.Failure;
        }

        var cloud = CloudConfig.Get(profile.Cloud);

        string token;
        try
        {
            token = await TokenProvider.GetAccessTokenAsync(profile, cloud, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            Console.Error.WriteLine("Your sign-in has expired or is missing. Run 'ppac auth create --deviceCode ...' again.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not get an access token: {ex.Message}");
            return ExitCodes.Failure;
        }

        try
        {
            using var http = new HttpClient();
            return await call(new PowerPlatformApi(http, token), cloud);
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine($"The request failed: {(int)ex.StatusCode} {ex.StatusCode}. {ex.Message}");
            if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Console.Error.WriteLine(forbiddenHint);
            }
            else if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Console.Error.WriteLine(notFoundHint);
            }
            else if (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Console.Error.WriteLine("The service is limiting requests. Wait a little and run the command again.");
            }
            return ExitCodes.Failure;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not reach the Power Platform API: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
