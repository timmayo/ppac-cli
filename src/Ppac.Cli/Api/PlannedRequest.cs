using System.Text.Json.Nodes;

namespace Ppac.Cli.Api;

/// <summary>
/// A request that is built but not yet sent. '--whatif' prints it and the real run
/// sends this same object, so the preview cannot differ from what is sent.
/// </summary>
public sealed record PlannedRequest(HttpMethod Method, string Url, JsonNode? Body = null);
