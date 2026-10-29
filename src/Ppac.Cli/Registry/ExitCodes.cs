namespace Ppac.Cli.Registry;

/// <summary>Exit codes used by every command.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The command ran but failed (sign-in, API error, nothing to act on).</summary>
    public const int Failure = 1;

    /// <summary>The command line was invalid (unknown command, missing or bad option).</summary>
    public const int Usage = 2;
}