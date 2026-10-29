using System.Net;

namespace Ppac.Cli.Api;

/// <summary>A non-success response from a Power Platform API call.</summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode statusCode, string message, string? body)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Body { get; }
}