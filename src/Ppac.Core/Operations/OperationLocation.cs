namespace Ppac.Core.Operations;

public static class OperationLocation
{
    /// <summary>
    /// Pulls the operation ID out of a Location (or Operation-Location) URL such as
    /// https://api.powerplatform.com/environmentmanagement/environments/{env}/operations/{id}?api-version=1.
    /// Returns null when the value is not a URL containing an "operations" segment followed by an ID.
    /// </summary>
    public static string? ParseOperationId(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) ||
            !Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
            if (string.Equals(segments[i], "operations", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(segments[i + 1]);

        return null;
    }
}