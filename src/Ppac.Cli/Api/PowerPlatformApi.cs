using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Ppac.Cli.Api;

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
        var (status, body) = await SendCoreAsync(HttpMethod.Get, url, null, cancellationToken);

        return JsonNode.Parse(body)
            ?? throw new ApiException(status, "The service returned an empty response.", body);
    }

    /// <summary>
    /// Sends a planned request. Returns the JSON response, or null when the response has no
    /// body (for example 204 No Content). Throws ApiException for a non-success status.
    /// </summary>
    public async Task<JsonNode?> SendAsync(PlannedRequest request, CancellationToken cancellationToken)
    {
        var (_, body) = await SendCoreAsync(request.Method, request.Url, request.Body, cancellationToken);
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendCoreAsync(
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

        return (response.StatusCode, body);
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
            foreach (var candidate in new[] { node?["detail"], node?["title"], node?["error"]?["message"] })
            {
                if (candidate is JsonValue value && value.TryGetValue<string>(out var text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON; fall through.
        }

        return body.Length <= 300 ? body : body[..300] + "...";
    }
}
