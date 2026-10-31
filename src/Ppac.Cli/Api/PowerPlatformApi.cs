using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Ppac.Cli.Api;

/// <summary>A successful response: status, headers (names are case-insensitive) and the JSON body, or null when there is none.</summary>
public sealed record ApiResponse(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Minimal authenticated JSON client for the Power Platform API.</summary>
public sealed class PowerPlatformApi
{
    private readonly HttpClient _http;
    private readonly string _accessToken;

    public PowerPlatformApi(HttpClient http, string accessToken)
    {
        _http = http;
        _accessToken = accessToken;
    }

    public async Task<JsonNode> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var (status, body, _) = await SendCoreAsync(HttpMethod.Get, url, null, cancellationToken);

        return JsonNode.Parse(body)
            ?? throw new ApiException(status, "The service returned an empty response.", body);
    }

    /// <summary>
    /// Sends a planned request. Returns the JSON response, or null when the response has no
    /// body (for example 204 No Content). Throws ApiException for a non-success status.
    /// </summary>
    public async Task<JsonNode?> SendAsync(PlannedRequest request, CancellationToken cancellationToken)
    {
        var (_, body, _) = await SendCoreAsync(request.Method, request.Url, request.Body, cancellationToken);
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    /// <summary>
    /// Like SendAsync, but also returns the status code and response headers. Needed for calls that
    /// answer 202 Accepted, where the headers may point at the operation.
    /// </summary>
    public async Task<ApiResponse> SendDetailedAsync(PlannedRequest request, CancellationToken cancellationToken)
    {
        var (status, body, headers) = await SendCoreAsync(request.Method, request.Url, request.Body, cancellationToken);
        return new ApiResponse(status, headers, string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body));
    }

    private async Task<(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers)> SendCoreAsync(
        HttpMethod method, string url, JsonNode? jsonBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(response.StatusCode, Describe(body), body);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return (response.StatusCode, body, headers);
    }

    // Documented error body: ProblemDetails (RFC 7807: type, title, status, detail, instance, extensions).
    // Only a 400 is documented, and live 404s came back with an empty body, so be lenient:
    // use detail, then title, then a conventional error.message, then the raw text.
    private static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No details were returned.";
        }

        try
        {
            var node = JsonNode.Parse(body);

            // A bare JSON string is the message itself (seen live: a rejected duplicate group name).
            if (Text(node) is { } plain)
            {
                return plain;
            }

            // Only an object has named properties; an array, number or null body falls through to the raw text.
            if (node is JsonObject obj)
            {
                // ValidationResponse (managed governance and similar): { errorDetail: { code, fieldErrors: { name: { errorMessages, suggestedValue } } } }
                if (obj["errorDetail"] is JsonObject validation && ValidationText(validation) is { } validationText)
                {
                    return validationText;
                }

                var error = obj["error"] as JsonObject;
                foreach (var candidate in new[] { obj["detail"], obj["title"], error?["message"], obj["error"], obj["message"] })
                {
                    if (Text(candidate) is { } text)
                    {
                        return text;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON; fall through.
        }

        return body.Length <= 300 ? body : body[..300] + "...";
    }

    private static string? ValidationText(JsonObject detail)
    {
        var parts = new List<string>();
        if (Text(detail["code"]) is { } code)
        {
            parts.Add(code);
        }

        if (detail["fieldErrors"] is JsonObject fields)
        {
            foreach (var (field, value) in fields)
            {
                var messages = (value?["errorMessages"] as JsonArray)?
                    .Select(Text)
                    .Where(m => m is not null)
                    .ToList() ?? new List<string?>();
                var text = messages.Count > 0 ? string.Join(" ", messages) : "invalid";
                if (Text(value?["suggestedValue"]) is { } suggested)
                {
                    text += $" (suggested value: {suggested})";
                }
                parts.Add($"{field}: {text}");
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
