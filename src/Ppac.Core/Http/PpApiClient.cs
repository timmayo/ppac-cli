using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ppac.Core.Http;

public interface ITokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

public sealed record ApiResponse(HttpStatusCode Status, string Body, JsonNode? Json);

/// <summary>
/// Thin client for api.powerplatform.com: bearer auth, retry with backoff (honors Retry-After),
/// OData paging, and mapping of HTTP failures to ppac exit codes.
/// </summary>
public sealed class PpApiClient
{
    public const string ApiHost = "api.powerplatform.com";
    public const string BaseUrl = "https://" + ApiHost;
    public const int MaxAttempts = 5;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    public PpApiClient(HttpClient http, ITokenProvider tokens,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? log = null)
    {
        _http = http;
        _tokens = tokens;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _log = log;
    }

    public async Task<ApiResponse> SendAsync(HttpMethod method, string pathOrUrl, string? jsonBody = null,
        CancellationToken ct = default)
    {
        var url = ResolveUrl(pathOrUrl);

        for (var attempt = 1; ; attempt++)
        {
            var token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxAttempts)
                    throw new PpacException(ExitCodes.General, $"Network error: {ex.Message}", inner: ex);
                await BackoffAsync(attempt, null, "network error", ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PpacException(ExitCodes.Timeout, "The request timed out.", inner: ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log?.Invoke($"{method} {url} -> {(int)response.StatusCode}");

                if (response.IsSuccessStatusCode)
                    return new ApiResponse(response.StatusCode, body, TryParse(body));

                if (IsRetryable(response.StatusCode) && attempt < MaxAttempts)
                {
                    await BackoffAsync(attempt, response.Headers.RetryAfter, $"HTTP {(int)response.StatusCode}", ct)
                        .ConfigureAwait(false);
                    continue;
                }

                throw MapError(response.StatusCode, body);
            }
        }
    }

    /// <summary>GETs a list endpoint and follows @odata.nextLink / nextLink until exhausted.</summary>
    public async Task<List<JsonObject>> GetAllAsync(string path, CancellationToken ct = default)
    {
        var items = new List<JsonObject>();
        string? next = path;

        for (var page = 0; next is not null; page++)
        {
            if (page >= 1000)
                throw new PpacException(ExitCodes.General, "Paging did not finish after 1000 pages.");

            var response = await SendAsync(HttpMethod.Get, next, null, ct).ConfigureAwait(false);
            next = null;
            if (response.Json is not JsonObject obj) break;

            if (obj["value"] is JsonArray values)
                foreach (var node in values)
                    if (node is JsonObject item)
                        items.Add(item);

            var link = obj["@odata.nextLink"] ?? obj["nextLink"];
            if (link is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                next = text;
        }

        return items;
    }

    private static string ResolveUrl(string pathOrUrl)
    {
        // On Unix, "/foo" parses as an absolute file URI, so check the scheme before treating it as a URL.
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (absolute.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(absolute.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
                throw new PpacException(ExitCodes.Usage,
                    $"Refusing to send credentials to '{absolute.Host}'. Only https://{ApiHost} is allowed.");
            return absolute.AbsoluteUri;
        }

        return BaseUrl + (pathOrUrl.StartsWith('/') ? "" : "/") + pathOrUrl;
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private Task BackoffAsync(int attempt, RetryConditionHeaderValue? retryAfter, string reason, CancellationToken ct)
    {
        TimeSpan wait;
        if (retryAfter?.Delta is { } delta) wait = delta;
        else if (retryAfter?.Date is { } date) wait = date - DateTimeOffset.UtcNow;
        else wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > MaxWait) wait = MaxWait;

        _log?.Invoke($"Retrying in {wait.TotalSeconds:0.#}s ({reason}, attempt {attempt} of {MaxAttempts})");
        return _delay(wait, ct);
    }

    private static PpacException MapError(HttpStatusCode status, string body)
    {
        var exitCode = status switch
        {
            HttpStatusCode.Unauthorized => ExitCodes.Auth,
            HttpStatusCode.Forbidden => ExitCodes.Forbidden,
            HttpStatusCode.NotFound => ExitCodes.NotFound,
            HttpStatusCode.Conflict => ExitCodes.Conflict,
            HttpStatusCode.TooManyRequests => ExitCodes.Throttled,
            _ => ExitCodes.General,
        };

        string? detail = null;
        if (TryParse(body) is JsonObject obj)
        {
            var error = obj["error"] as JsonObject ?? obj;
            detail = string.Join(": ", new[] { Text(error["code"]), Text(error["message"]) }
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        var message = $"HTTP {(int)status} {status}" + (string.IsNullOrEmpty(detail) ? "" : $" - {detail}");
        return new PpacException(exitCode, message, body);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }
}
