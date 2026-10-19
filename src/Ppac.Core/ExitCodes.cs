namespace Ppac.Core;

/// <summary>Process exit codes. Part of the scriptability contract (DESIGN.md 4.x); do not renumber.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int General = 1;
    public const int Usage = 2;
    public const int Auth = 3;
    public const int NotFound = 4;
    public const int Forbidden = 5;
    public const int Conflict = 6;
    public const int Throttled = 7;
    public const int AsyncFailed = 8;
    public const int Timeout = 9;
}

/// <summary>An expected failure that maps to a specific exit code and a one-line message.</summary>
public sealed class PpacException : Exception
{
    public int ExitCode { get; }

    /// <summary>Raw detail (for example the response body). Shown only with --verbose.</summary>
    public string? Details { get; }

    public PpacException(int exitCode, string message, string? details = null, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
        Details = details;
    }
}
