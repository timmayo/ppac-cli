using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ppac.Cli.Output;

/// <summary>
/// One place that reports a failure. Human text always goes to stderr. With --json, a single
/// JSON object also goes to stdout so scripts and agents get the detail without parsing text:
/// {"error":{"exitCode":3,"status":403,"title":"Forbidden","detail":"...","hint":"...","details":{...}}}
/// "detail" is the service's own message when there is one; the stderr text is the longer sentence.
/// </summary>
public static partial class ErrorReport
{
    /// <summary>Readable name for an HTTP status: NotFound becomes "Not Found".</summary>
    public static string StatusTitle(HttpStatusCode status) =>
        WordBreak().Replace(status.ToString(), " $1");

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex WordBreak();

    public static int Write(
        int exitCode,
        string message,
        bool json,
        int? status = null,
        string? title = null,
        string? hint = null,
        JsonObject? details = null,
        string? detail = null)
    {
        Console.Error.WriteLine(message);
        if (!string.IsNullOrEmpty(hint))
        {
            Console.Error.WriteLine(hint);
        }

        if (json)
        {
            var error = new JsonObject { ["exitCode"] = exitCode };
            if (status is not null) error["status"] = status;
            if (!string.IsNullOrEmpty(title)) error["title"] = title;
            error["detail"] = detail ?? message;
            if (!string.IsNullOrEmpty(hint)) error["hint"] = hint;
            if (details is not null) error["details"] = details;
            JsonOutput.Write(new JsonObject { ["error"] = error });
        }

        return exitCode;
    }
}
