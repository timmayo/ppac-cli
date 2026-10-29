using System.Net.Http.Headers;
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
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(response.StatusCode, Describe(body), body);
        }

        return JsonNode.Parse(body)
            ?? throw new ApiException(response.StatusCode, "The service returned an empty response.", body);
    }

    // The error body shape is not documented for this API, so look for a
    // conventional error.message and otherwise fall back to the raw text.
    private static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No details were returned.";
        }

        try
        {
            if (JsonNode.Parse(body)?["error"]?["message"] is JsonNode message)
            {
                return message.ToString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON; fall through.
        }

        return body.Length <= 300 ? body : body[..300] + "...";
    }
}