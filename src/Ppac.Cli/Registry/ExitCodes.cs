using System.Net;

namespace Ppac.Cli.Registry;

/// <summary>
/// Exit codes used by every command. They are a contract for scripts: add new codes, never renumber.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The command ran but failed in a way that has no more specific code (for example a 400).</summary>
    public const int Failure = 1;

    /// <summary>The command line was invalid (unknown command, missing or bad option).</summary>
    public const int Usage = 2;

    /// <summary>Not signed in, sign-in expired, or the service refused access (401/403). Retrying will not help.</summary>
    public const int AuthRequired = 3;

    /// <summary>The thing to act on was not found (404, empty 204 on a get, backup not in the list).</summary>
    public const int NotFound = 4;

    /// <summary>Temporary problem: throttled (429), timeout (408), server error (5xx) or network failure. Retrying may work.</summary>
    public const int Retryable = 5;

    /// <summary>The person answered no at the confirmation prompt. Nothing was changed.</summary>
    public const int Declined = 6;

    /// <summary>Maps an HTTP error status to an exit code.</summary>
    public static int ForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AuthRequired,
        HttpStatusCode.NotFound => NotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => Retryable,
        >= HttpStatusCode.InternalServerError => Retryable,
        _ => Failure
    };

    /// <summary>Short name for an exit code, used in 'ppac commands --json' and error output.</summary>
    public static string Describe(int code) => code switch
    {
        Success => "success",
        Failure => "the command ran but failed",
        Usage => "invalid command line",
        AuthRequired => "not signed in, or access refused (401/403)",
        NotFound => "not found",
        Retryable => "temporary failure (throttled, server error or network); retrying may work",
        Declined => "declined at the confirmation prompt; nothing was changed",
        _ => "unknown"
    };

    public static readonly IReadOnlyList<int> All =
        new[] { Success, Failure, Usage, AuthRequired, NotFound, Retryable, Declined };
}
