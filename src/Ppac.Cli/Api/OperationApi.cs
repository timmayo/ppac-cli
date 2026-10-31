using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>
/// Operation status (Environment Management API, api-version 2024-10-01). Source: Learn REST reference, read 2026-10-30.
/// Environment operation: GET /environmentmanagement/environments/{targetEnvironmentId}/operations/{operationId}
///   Documents 200 (OperationExecutionResult), 400 (ValidationResponse), 401, 403, 404, 429. No 204. Only the .default scope is listed.
///   OperationExecutionResult: name, operationId, status, startTime, endTime, requestedBy, stageStatuses[], errorDetail, updatedEnvironment.
///   OperationStatus: Queued, InProgress, Succeeded, ValidationFailed, Failed, NoOperation, ValidationPassed.
/// Environment group operation: GET /environmentmanagement/environmentGroupOperations/{operationId}
///   Documents 200 (body schema NOT defined on the page), 204 and 400 (ProblemDetails). Only the .default scope is listed.
/// Live (2026-10-30) the enable/disable and add/remove calls returned an Operation-Location header pointing at these routes
/// with api-version=1 (not the documented 2024-10-01). ppac uses the documented version; whether the service accepts the
/// operation IDs it hands out under 2024-10-01 is untested until the commands are run live.
/// </summary>
public static class OperationApi
{
    public const string ApiVersion = "2024-10-01";

    public static PlannedRequest EnvironmentOperationRequest(CloudConfig cloud, string environmentId, string operationId) =>
        new(HttpMethod.Get,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environments/{Uri.EscapeDataString(environmentId)}/operations/{Uri.EscapeDataString(operationId)}?api-version={ApiVersion}");

    public static PlannedRequest GroupOperationRequest(CloudConfig cloud, string operationId) =>
        new(HttpMethod.Get,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroupOperations/{Uri.EscapeDataString(operationId)}?api-version={ApiVersion}");

    /// <summary>
    /// Accepts a bare operation ID or the Operation-Location URL a change printed
    /// (https://.../environments/{environmentId}/operations/{operationId}?api-version=1). From a URL it also returns the environment ID.
    /// </summary>
    public static bool TryParseEnvironmentOperation(string value, out string? environmentId, out string operationId)
    {
        environmentId = null;
        operationId = "";
        value = value.Trim();

        if (TryUrlSegments(value, out var segments))
        {
            var at = Array.FindIndex(segments, s => s.Equals("environments", StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at + 3 >= segments.Length ||
                !segments[at + 2].Equals("operations", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            environmentId = segments[at + 1];
            operationId = segments[at + 3];
            return true;
        }

        return IsBareId(value, out operationId);
    }

    /// <summary>Accepts a bare operation ID or an .../environmentGroupOperations/{operationId} URL.</summary>
    public static bool TryParseGroupOperation(string value, out string operationId)
    {
        operationId = "";
        value = value.Trim();

        if (TryUrlSegments(value, out var segments))
        {
            var at = Array.FindIndex(segments, s => s.Equals("environmentGroupOperations", StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at + 1 >= segments.Length)
            {
                return false;
            }
            operationId = segments[at + 1];
            return true;
        }

        return IsBareId(value, out operationId);
    }

    private static bool TryUrlSegments(string value, out string[] segments)
    {
        segments = Array.Empty<string>();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return true;
        }
        return false;
    }

    private static bool IsBareId(string value, out string id)
    {
        id = value;
        return value.Length > 0 && !value.Any(c => c == '/' || c == '?' || c == '&' || char.IsWhiteSpace(c));
    }
}
