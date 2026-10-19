using System.CommandLine;
using Ppac.Cli.Auth;
using Ppac.Core;
using Ppac.Core.Config;
using Ppac.Core.Http;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

/// <summary>Options available on every command.</summary>
public static class GlobalOptions
{
    public static readonly Option<string> Output = new("--output", "-o")
    {
        Description = "Output format: table, json, jsonl, csv, tsv",
        DefaultValueFactory = _ => "table",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose")
    {
        Description = "Print HTTP requests, retries and error details to stderr",
        Recursive = true,
    };

    public static readonly Option<string> Profile = new("--profile")
    {
        Description = "Authentication profile to use for this command (or PPAC_PROFILE). See: ppac auth list",
        Recursive = true,
    };

    public static OutputFormat Format(ParseResult parse) =>
        OutputWriter.ParseFormatOrThrow(parse.GetValue(Output));
}

public static class CommandRunner
{
    /// <summary>
    /// Runs a command body and turns failures into a one-line message on stderr plus the
    /// documented exit code. stdout stays clean for data.
    /// </summary>
    public static async Task<int> RunAsync(ParseResult parse, Func<CancellationToken, Task<int>> body, CancellationToken ct)
    {
        try
        {
            return await body(ct);
        }
        catch (PpacException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (parse.GetValue(GlobalOptions.Verbose) && !string.IsNullOrWhiteSpace(ex.Details))
                Console.Error.WriteLine(ex.Details);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: cancelled");
            return ExitCodes.General;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (parse.GetValue(GlobalOptions.Verbose)) Console.Error.WriteLine(ex);
            return ExitCodes.General;
        }
    }

    /// <summary>Builds an API client from the saved profile, flags and environment.</summary>
    public static PpApiClient CreateClient(ParseResult parse)
    {
        var profile = new ProfileStore().Resolve(null, null, Environment.GetEnvironmentVariable, parse.GetValue(GlobalOptions.Profile));
        var verbose = parse.GetValue(GlobalOptions.Verbose);
        return new PpApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(100) },
            new MsalAuth(profile),
            log: verbose ? message => Console.Error.WriteLine(message) : null);
    }
}