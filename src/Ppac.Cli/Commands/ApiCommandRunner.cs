using System.Net;
using Microsoft.Identity.Client;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
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
    /// <param name="json">When true, failures also write a JSON error object to stdout (see ErrorReport).</param>
    public static async Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call,
        string forbiddenHint,
        string notFoundHint,
        CancellationToken cancellationToken,
        bool json = false)
    {
        var profile = ProfileStore.GetActive();
        if (profile is null)
        {
            return ErrorReport.Write(
                ExitCodes.AuthRequired,
                "No active profile. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.",
                json);
        }

        var cloud = CloudConfig.Get(profile.Cloud);

        string token;
        try
        {
            token = await TokenProvider.GetAccessTokenAsync(profile, cloud, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            return ErrorReport.Write(
                ExitCodes.AuthRequired,
                "Your sign-in has expired or is missing. Run 'ppac auth create --deviceCode ...' again.",
                json);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is MsalClientException { ErrorCode: "http_request_failed" })
        {
            return ErrorReport.Write(
                ExitCodes.Retryable, $"Could not reach the sign-in service: {ex.Message}", json);
        }
        catch (Exception ex)
        {
            return ErrorReport.Write(
                ExitCodes.AuthRequired, $"Could not get an access token: {ex.Message}", json);
        }

        try
        {
            using var http = new HttpClient();
            return await call(new PowerPlatformApi(http, token), cloud);
        }
        catch (ApiException ex)
        {
            var status = (int)ex.StatusCode;
            var title = ErrorReport.StatusTitle(ex.StatusCode);
            var hint = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => forbiddenHint,
                HttpStatusCode.NotFound => notFoundHint,
                HttpStatusCode.TooManyRequests =>
                    "The service is limiting requests. Wait a little and run the command again.",
                _ => null
            };
            return ErrorReport.Write(
                ExitCodes.ForStatus(ex.StatusCode),
                $"The request failed: {status} {title}. {ex.Message}",
                json,
                status,
                title,
                hint,
                detail: ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return ErrorReport.Write(
                ExitCodes.Retryable, $"Could not reach the Power Platform API: {ex.Message}", json);
        }
    }
}
