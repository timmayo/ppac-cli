using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Safety;

public enum ConfirmResult
{
    /// <summary>Go ahead (--yes, or the person answered yes).</summary>
    Proceed,

    /// <summary>The person answered no. Nothing was changed.</summary>
    Declined,

    /// <summary>Confirmation is needed but input is not interactive and --yes was not given.</summary>
    Refused
}

/// <summary>
/// Shared safety behavior for commands that change things.
/// --whatif: print the exact request and change nothing (never prompts).
/// --yes (destructive commands only): skip the confirmation prompt. Without it, a destructive
/// command asks first, and refuses to run when input is not interactive.
/// </summary>
public static class WriteSafety
{
    public const string WhatIfName = "--whatif";
    public const string YesName = "--yes";

    public static readonly OptionSpec WhatIfOption = new(
        WhatIfName, Array.Empty<string>(), OptionType.Bool,
        "Show what would happen and the exact request, and make no changes.");

    public static readonly OptionSpec YesOption = new(
        YesName, Array.Empty<string>(), OptionType.Bool,
        "Do not ask for confirmation. Required when running unattended or with redirected input.");

    /// <summary>
    /// True when a person can answer a prompt: input and error output are not redirected.
    /// The prompt is written to stderr so stdout stays clean for --json.
    /// Tests replace this.
    /// </summary>
    internal static Func<bool> IsInteractive { get; set; } =
        () => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    internal static ConfirmResult Confirm(
        string message, bool yes, bool interactive, TextReader input, TextWriter prompt)
    {
        if (yes)
        {
            return ConfirmResult.Proceed;
        }

        if (!interactive)
        {
            return ConfirmResult.Refused;
        }

        prompt.Write($"{message} Continue? [y/N] ");
        prompt.Flush();

        var answer = input.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            ? ConfirmResult.Proceed
            : ConfirmResult.Declined;
    }

    public static void WriteRefusal(string action) =>
        Console.Error.WriteLine(
            $"{action} needs confirmation, but input is not interactive. " +
            "Add --yes to run it without a prompt, or --whatif to preview it.");

    /// <summary>
    /// Writes the --whatif result. In text mode: a summary, optional detail lines, and the request.
    /// In --json mode: one JSON object on stdout.
    /// </summary>
    public static void WriteWhatIf(
        string command,
        string summary,
        PlannedRequest request,
        bool json,
        IReadOnlyList<string>? lines = null,
        JsonObject? details = null)
    {
        if (json)
        {
            var result = new JsonObject
            {
                ["whatIf"] = true,
                ["command"] = command,
                ["summary"] = summary,
                ["request"] = new JsonObject
                {
                    ["method"] = request.Method.Method,
                    ["url"] = request.Url,
                    ["body"] = request.Body?.DeepClone()
                }
            };
            if (details is not null)
            {
                result["details"] = details;
            }
            JsonOutput.Write(result);
            return;
        }

        Console.WriteLine("What if: no changes were made.");
        Console.WriteLine($"Would: {summary}");
        if (lines is not null)
        {
            foreach (var line in lines)
            {
                Console.WriteLine($"  {line}");
            }
        }
        Console.WriteLine($"Request: {request.Method.Method} {request.Url}");
        if (request.Body is not null)
        {
            Console.WriteLine($"Body: {request.Body.ToJsonString()}");
        }
    }
}
