using System.Text.Json.Nodes;
using Ppac.Core.Output;

namespace Ppac.Core.Operations;

/// <summary>
/// Status values from the Power Platform API operation resource (api-version 2024-10-01).
/// Queued and InProgress are the only non-terminal values documented. Treating NoOperation and
/// ValidationPassed as finished successfully is an assumption to confirm against live runs.
/// </summary>
public static class OperationStatus
{
    public static bool IsSuccess(string? status) => status is "Succeeded" or "NoOperation" or "ValidationPassed";

    public static bool IsFailure(string? status) => status is "Failed" or "ValidationFailed";

    /// <summary>
    /// Error text for a failed operation, formatted as " (Code: message)" or "" when there is none.
    /// A live Create operation put the error on the failed entry of stageStatuses (errorDetail.code plus
    /// fieldErrors.&lt;code&gt;.errorMessages), not at the top level, so look there first, then at the top level.
    /// </summary>
    public static string FailureDetail(JsonObject operation)
    {
        JsonObject? detail = null;

        if (operation["stageStatuses"] is JsonArray stages)
            foreach (var stage in stages)
                if (stage is JsonObject s && IsFailure(OutputWriter.CellText(s, "status")) &&
                    s["errorDetail"] is JsonObject d)
                {
                    detail = d;
                    break;
                }

        detail ??= operation["errorDetail"] as JsonObject;
        if (detail is null) return "";

        var code = OutputWriter.CellText(detail, "code");
        string message = "";
        if (code.Length > 0 && detail["fieldErrors"]?[code]?["errorMessages"] is JsonArray messages)
            message = string.Join(" ", messages.Select(m => m?.GetValue<string>()).Where(m => !string.IsNullOrEmpty(m)));

        if (code.Length == 0 && message.Length == 0) return "";
        return " (" + (message.Length > 0 ? $"{code}: {message}" : code) + ")";
    }
}

public static class OperationWaiter
{
    /// <summary>
    /// Polls until the operation finishes. Returns the final operation on success, throws exit 8 on
    /// failure and exit 9 on timeout (the operation keeps running on the service).
    /// </summary>
    public static async Task<JsonObject> WaitAsync(
        Func<CancellationToken, Task<JsonObject>> fetch,
        TimeSpan interval,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> now,
        Action<string>? log,
        CancellationToken ct)
    {
        var started = now();

        while (true)
        {
            var operation = await fetch(ct).ConfigureAwait(false);
            var status = OutputWriter.CellText(operation, "status");

            if (OperationStatus.IsSuccess(status)) return operation;

            if (OperationStatus.IsFailure(status))
            {
                throw new PpacException(ExitCodes.AsyncFailed,
                    $"Operation {status}" + OperationStatus.FailureDetail(operation), operation.ToJsonString());
            }

            if ((now() - started) + interval > timeout)
                throw new PpacException(ExitCodes.Timeout,
                    $"Timed out after {timeout.TotalSeconds:0.#}s; the operation is still '{status}' and keeps running. Check it with: ppac op show");

            log?.Invoke($"status: {(status.Length > 0 ? status : "unknown")}; checking again in {interval.TotalSeconds:0.#}s");
            await delay(interval, ct).ConfigureAwait(false);
        }
    }
}