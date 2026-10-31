using System.Text.Json.Nodes;
using Ppac.Cli.Config;

namespace Ppac.Cli.Api;

/// <summary>
/// Environment group operations (Environment Management API, api-version 2024-10-01, not labeled preview).
/// List: GET /environmentmanagement/environmentGroups  (tenant level; paging via @odata.nextLink)
/// Get:  GET /environmentmanagement/environmentGroups/{groupId}
/// Create: POST /environmentmanagement/environmentGroups  body: EnvironmentGroup (displayName, description). The reference marks no property
/// as required, but the service answered 400 "You must provide a display name and description for the environment group."
/// when the description was missing (live 2026-10-30), so both are required. It allows duplicate display names (live 2026-10-30).
/// Delete: DELETE /environmentmanagement/environmentGroups/{groupId}
/// Add environment:    POST /environmentmanagement/environmentGroups/{groupId}/addEnvironment/{environmentId}   (no body)
/// Remove environment: POST /environmentmanagement/environmentGroups/{groupId}/removeEnvironment/{environmentId}   (no body)
/// Add and remove document 202 Accepted, 204 and 400 (ProblemDetails); no response headers are documented.
/// Create documents 200 (EnvironmentGroup), 204 and 400 (ProblemDetails); Delete documents 200, 204 and 400.
/// No operation ID or Operation-Location header is documented for either.
/// Both reference pages list only the .default scope; the permission reference names
/// EnvironmentManagement.Groups.Read ("Read environment groups").
/// A 204 No Content is a documented response for both calls.
/// </summary>
public static class EnvironmentGroupApi
{
    public const string ApiVersion = "2024-10-01";

    public static PlannedRequest ListRequest(CloudConfig cloud) =>
        new(HttpMethod.Get, $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups?api-version={ApiVersion}");

    public static PlannedRequest GetRequest(CloudConfig cloud, string groupId) =>
        new(HttpMethod.Get,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups/{Uri.EscapeDataString(groupId)}?api-version={ApiVersion}");

    /// <summary>Only displayName and description are sent. parentGroupId is left out: the admin docs say groups cannot be nested.</summary>
    public static PlannedRequest CreateRequest(CloudConfig cloud, string displayName, string description) =>
        new(HttpMethod.Post,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups?api-version={ApiVersion}",
            new JsonObject { ["displayName"] = displayName, ["description"] = description });

    public static PlannedRequest AddEnvironmentRequest(CloudConfig cloud, string groupId, string environmentId) =>
        MembershipRequest(cloud, groupId, "addEnvironment", environmentId);

    public static PlannedRequest RemoveEnvironmentRequest(CloudConfig cloud, string groupId, string environmentId) =>
        MembershipRequest(cloud, groupId, "removeEnvironment", environmentId);

    private static PlannedRequest MembershipRequest(CloudConfig cloud, string groupId, string action, string environmentId) =>
        new(HttpMethod.Post,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups/{Uri.EscapeDataString(groupId)}/{action}/{Uri.EscapeDataString(environmentId)}?api-version={ApiVersion}");

    /// <summary>
    /// Is the environment in the group? Uses the environment list filter on environmentGroupId (a documented
    /// filter property). Returns null when the service rejects that filter (400), because then membership
    /// cannot be checked this way. The exact filter syntax is unverified until run live.
    /// </summary>
    public static async Task<bool?> IsMemberAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, string environmentId, CancellationToken cancellationToken)
    {
        var members = await MembersAsync(api, cloud, groupId, cancellationToken);
        return members?.Any(m => string.Equals(
            m?["id"]?.ToString(), environmentId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The environments in a group (same filter as IsMemberAsync; verified live 2026-10-30). Null when the
    /// service rejects the filter (400). The service refuses to delete a group that still holds environments
    /// (400 "Request failed with status code Conflict"; verified live), so delete checks this first.
    /// </summary>
    public static async Task<JsonArray?> MembersAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, CancellationToken cancellationToken)
    {
        try
        {
            return await EnvironmentApi.ListAsync(
                api, cloud, $"environmentGroupId eq '{groupId}'", null, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    /// <summary>How many back-to-back reads a decision is based on. See <see cref="SampleMembersAsync"/>.</summary>
    public const int ReadSamples = 6;

    /// <summary>What repeated reads of a group's member list said about one environment.</summary>
    public enum MembershipVerdict { Member, NotMember, Unclear }

    /// <summary>
    /// The result of <see cref="ReadSamples"/> back-to-back reads of a group's member list.
    /// </summary>
    public sealed class MemberSamples
    {
        private readonly List<JsonArray> _reads;

        public MemberSamples(List<JsonArray> reads) => _reads = reads;

        public int Count => _reads.Count;

        /// <summary>Every environment seen in any read (a read can miss a member, so the union is the safe list).</summary>
        public List<(string Id, string Name)> Union()
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var read in _reads)
            {
                foreach (var item in read)
                {
                    var id = item?["id"]?.ToString();
                    if (id is not null && !seen.ContainsKey(id))
                    {
                        seen[id] = item?["displayName"]?.ToString() ?? "-";
                    }
                }
            }
            return seen.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        /// <summary>How many of the reads listed the environment.</summary>
        public int ReadsWith(string environmentId) =>
            _reads.Count(read => read.Any(m => string.Equals(m?["id"]?.ToString(), environmentId, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Member when every read lists it, NotMember when none does, otherwise Unclear.</summary>
        public MembershipVerdict Verdict(string environmentId)
        {
            var with = ReadsWith(environmentId);
            return with == Count ? MembershipVerdict.Member : with == 0 ? MembershipVerdict.NotMember : MembershipVerdict.Unclear;
        }
    }

    /// <summary>
    /// Reads the group's member list <see cref="ReadSamples"/> times back to back. Live (2026-10-30,
    /// probe-read-consistency.ps1) the same filter said "not a member" for a member in 6 of 20 reads while nothing was
    /// changing, and some answers were snapshots minutes old. A single read cannot justify "not a member" or "no
    /// environments", so decisions use all the reads. Null when the service rejects the filter (400).
    /// </summary>
    public static async Task<MemberSamples?> SampleMembersAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, CancellationToken cancellationToken)
    {
        var reads = new List<JsonArray>();
        for (var i = 0; i < ReadSamples; i++)
        {
            var members = await MembersAsync(api, cloud, groupId, cancellationToken);
            if (members is null)
            {
                return null;
            }
            reads.Add(members);
        }
        return new MemberSamples(reads);
    }

    public static PlannedRequest DeleteRequest(CloudConfig cloud, string groupId) =>
        new(HttpMethod.Delete,
            $"{cloud.ApiBaseUrl}/environmentmanagement/environmentGroups/{Uri.EscapeDataString(groupId)}?api-version={ApiVersion}");

    /// <summary>
    /// Lists groups, following @odata.nextLink until all pages (or top) are read.
    /// No $top is documented, so top is applied here and paging stops once enough are read.
    /// A 204 (no body) is an empty list.
    /// </summary>
    public static async Task<JsonArray> ListAsync(
        PowerPlatformApi api, CloudConfig cloud, int? top, CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        string? url = ListRequest(cloud).Url;

        while (url is not null)
        {
            var page = await api.SendAsync(new PlannedRequest(HttpMethod.Get, url), cancellationToken);

            if (page?["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    all.Add(item?.DeepClone());
                }
            }

            if (top is not null && all.Count >= top.Value)
            {
                while (all.Count > top.Value)
                {
                    all.RemoveAt(all.Count - 1);
                }
                break;
            }

            var next = (page?["@odata.nextLink"] ?? page?["@odata.nextlink"])?.ToString();
            url = string.IsNullOrWhiteSpace(next) || next == url ? null : next;
        }

        return all;
    }

    /// <summary>Gets one group. Returns null for a 204 No Content. Throws ApiException (for example 404).</summary>
    public static Task<JsonNode?> GetAsync(
        PowerPlatformApi api, CloudConfig cloud, string groupId, CancellationToken cancellationToken) =>
        api.SendAsync(GetRequest(cloud, groupId), cancellationToken);
}
