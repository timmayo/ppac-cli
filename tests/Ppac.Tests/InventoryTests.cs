using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Http;
using Ppac.Core.Inventory;
using Ppac.Core.Output;

namespace Ppac.Tests;

public class InventoryTests
{
    // Shape copied from a live POST /resourcequery/resources/query response (2024-10-01), values anonymized.
    private const string FlowRow = """
        {
          "id": "/providers/Microsoft.PowerPlatform/environments/00000000-0000-0000-0000-0000000000e1/providers/Microsoft.PowerAutomate/cloudFlows/00000000-0000-0000-0000-0000000000f1",
          "name": "00000000-0000-0000-0000-0000000000f1",
          "type": "microsoft.powerautomate/cloudflows",
          "tenantId": "00000000-0000-0000-0000-0000000000aa",
          "location": "unitedstates",
          "properties": {
            "powerPlatformConnectors": [
              { "connectorId": "shared_office365", "operations": [ { "operationId": "SendEmailV2" } ] },
              { "connectorId": "shared_commondataserviceforapps", "operations": [ { "operationId": "GetItem" } ] },
              { "connectorId": "shared_office365", "operations": [ { "operationId": "GetMessage" } ] }
            ],
            "displayName": "Export Solution to GitHub",
            "environmentId": "00000000-0000-0000-0000-0000000000e1",
            "createdAt": "2026-08-24T23:50:52Z",
            "ownerId": "00000000-0000-0000-0000-0000000000bb",
            "lastModifiedAt": "2026-08-25T13:53:09.6758391Z",
            "workflowEntityId": "00000000-0000-0000-0000-0000000000c1",
            "status": "Activated"
          }
        }
        """;

    private static JsonObject Row(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly Dictionary<string, EnvironmentInfo> Envs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00000000-0000-0000-0000-0000000000e1"] = new("Contoso Dev", "Sandbox"),
    };

    [Fact]
    public void No_type_means_apps_flows_and_agents()
    {
        var types = InventoryQuery.ResolveTypes(null);
        Assert.Contains("microsoft.powerapps/canvasapps", types);
        Assert.Contains("microsoft.powerautomate/cloudflows", types);
        Assert.Contains("microsoft.copilotstudio/agents", types);
        Assert.DoesNotContain("microsoft.powerplatformconnector/connectors", types);
    }

    [Fact]
    public void Type_names_pick_their_resource_types_ignoring_case()
    {
        Assert.Equal("microsoft.powerapps/canvasapps", string.Join(",", InventoryQuery.ResolveTypes("Canvas-App")));
        Assert.Equal(3, InventoryQuery.ResolveTypes("flow").Count);
    }

    [Fact]
    public void An_unknown_type_is_a_usage_error_that_lists_the_choices()
    {
        var ex = Assert.Throws<PpacException>(() => InventoryQuery.ResolveTypes("widget"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("canvas-app", ex.Message);
    }

    [Fact]
    public void The_request_has_the_documented_shape()
    {
        var body = InventoryQuery.BuildRequest(["microsoft.powerapps/canvasapps"], null, null, 50, null);

        Assert.Equal("PowerPlatformResources", (string)body["TableName"]!);
        Assert.Equal(50, (int)body["Options"]!["Top"]!);
        Assert.True(body["Options"]!["Skip"] is null);
        Assert.True(body["Options"]!["SkipToken"] is null);

        var clauses = (JsonArray)body["Clauses"]!;
        Assert.Equal(2, clauses.Count);
        Assert.Equal("where", (string)clauses[0]!["$type"]!);
        Assert.Equal("type", (string)clauses[0]!["FieldName"]!);
        Assert.Equal("in~", (string)clauses[0]!["Operator"]!);
        Assert.Equal("'microsoft.powerapps/canvasapps'", (string)clauses[0]!["Values"]![0]!);
        Assert.Equal("orderby", (string)clauses[1]!["$type"]!);
    }

    [Fact]
    public void Environment_and_owner_filters_are_added_lowercased_and_quoted()
    {
        var body = InventoryQuery.BuildRequest(["t"], "ABC-123", "00000000-0000-0000-0000-0000000000BB", 10, "tok");
        var clauses = (JsonArray)body["Clauses"]!;

        Assert.Equal(4, clauses.Count);
        Assert.Equal("properties.environmentId", (string)clauses[1]!["FieldName"]!);
        Assert.Equal("'abc-123'", (string)clauses[1]!["Values"]![0]!);
        Assert.Equal("properties.ownerId", (string)clauses[2]!["FieldName"]!);
        Assert.Equal("'00000000-0000-0000-0000-0000000000bb'", (string)clauses[2]!["Values"]![0]!);
        Assert.Equal("tok", (string)body["Options"]!["SkipToken"]!);
        Assert.True(body["Options"]!["Skip"] is null);
    }

    [Theory]
    [InlineData("x' or 1=1")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    public void Filter_values_that_could_break_the_query_are_rejected(string value)
    {
        var ex = Assert.Throws<PpacException>(() => InventoryQuery.BuildRequest(["t"], value, null, 10, null));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public void Flatten_reads_a_live_shaped_row_and_adds_the_environment()
    {
        var flat = InventoryQuery.Flatten(Row(FlowRow), Envs);

        Assert.Equal("00000000-0000-0000-0000-0000000000f1", (string)flat["id"]!);
        Assert.Equal("Export Solution to GitHub", (string)flat["displayName"]!);
        Assert.Equal("Cloud flow", (string)flat["type"]!);
        Assert.Equal("microsoft.powerautomate/cloudflows", (string)flat["resourceType"]!);
        Assert.Equal("Contoso Dev", (string)flat["environmentName"]!);
        Assert.Equal("Sandbox", (string)flat["environmentType"]!);
        Assert.Equal("00000000-0000-0000-0000-0000000000bb", (string)flat["ownerId"]!);
        Assert.Equal("2026-08-25T13:53:09.6758391Z", (string)flat["lastModifiedAt"]!);
        Assert.Equal("Activated", (string)flat["status"]!);
        Assert.Equal("shared_commondataserviceforapps,shared_office365", (string)flat["connectors"]!);
    }

    [Fact]
    public void Flatten_leaves_environment_fields_empty_when_the_environment_is_unknown()
    {
        var flat = InventoryQuery.Flatten(Row(FlowRow), new Dictionary<string, EnvironmentInfo>());
        Assert.Equal("", (string)flat["environmentName"]!);
        Assert.Equal("00000000-0000-0000-0000-0000000000e1", (string)flat["environmentId"]!);
    }

    [Fact]
    public void Inventory_columns_read_the_flattened_fields()
    {
        var flat = InventoryQuery.Flatten(Row(FlowRow), Envs);
        var values = Columns.Inventory.ToDictionary(c => c.Header, c => OutputWriter.CellText(flat, c.Path));

        Assert.Equal("Export Solution to GitHub", values["Name"]);
        Assert.Equal("Cloud flow", values["Type"]);
        Assert.Equal("Contoso Dev", values["Environment"]);
        Assert.Equal("Activated", values["Status"]);
    }

    private static string Page(string[] names, string? skipToken, int total) =>
        new JsonObject
        {
            ["totalRecords"] = total,
            ["count"] = names.Length,
            ["resultTruncated"] = 1,
            ["skipToken"] = skipToken,
            ["data"] = new JsonArray(names.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n,
                ["type"] = "microsoft.powerapps/canvasapps",
                ["properties"] = new JsonObject { ["displayName"] = n },
            }).ToArray()),
        }.ToJsonString();

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(string body, List<string>? bodies = null) =>
        request =>
        {
            bodies?.Add(request.Content!.ReadAsStringAsync().Result);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        };

    private static PpApiClient Client(StubHandler handler) =>
        new(new HttpClient(handler), new StubTokens(), (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Paging_follows_the_skip_token_until_it_is_empty()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(
            Respond(Page(["a", "b"], "t1", 3), bodies),
            Respond(Page(["c"], null, 3), bodies));

        var result = await InventoryQuery.QueryAsync(Client(handler), ["microsoft.powerapps/canvasapps"], null, null, null, 2, default);

        Assert.Equal("a,b,c", string.Join(",", result.Rows.Select(r => (string)r["name"]!)));
        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(2, bodies.Count);
        Assert.True(JsonNode.Parse(bodies[0])!["Options"]!["SkipToken"] is null);
        Assert.Equal("t1", (string)JsonNode.Parse(bodies[1])!["Options"]!["SkipToken"]!);
        Assert.True(JsonNode.Parse(bodies[1])!["Options"]!["Skip"] is null);
    }

    [Fact]
    public async Task A_repeated_skip_token_stops_with_an_error_instead_of_looping()
    {
        var handler = new StubHandler(
            Respond(Page(["a"], "same", 9)),
            Respond(Page(["a"], "same", 9)));

        var ex = await Assert.ThrowsAsync<PpacException>(() =>
            InventoryQuery.QueryAsync(Client(handler), ["t"], null, null, null, 1, default));

        Assert.Equal(ExitCodes.General, ex.ExitCode);
        Assert.Contains("same page token", ex.Message);
    }

    [Fact]
    public async Task A_limit_stops_paging_and_asks_for_no_more_rows_than_needed()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(Respond(Page(["a", "b"], "more", 50), bodies));

        var result = await InventoryQuery.QueryAsync(Client(handler), ["t"], null, null, 2, 1000, default);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(50, result.TotalRecords);
        Assert.Single(bodies);
        Assert.Equal(2, (int)JsonNode.Parse(bodies[0])!["Options"]!["Top"]!);
    }

    [Fact]
    public async Task A_limit_across_pages_trims_to_the_limit()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(
            Respond(Page(["a", "b"], "t1", 9), bodies),
            Respond(Page(["c", "d"], "t2", 9), bodies));

        var result = await InventoryQuery.QueryAsync(Client(handler), ["t"], null, null, 3, 2, default);

        Assert.Equal("a,b,c", string.Join(",", result.Rows.Select(r => (string)r["name"]!)));
        Assert.Equal(1, (int)JsonNode.Parse(bodies[1])!["Options"]!["Top"]!);
    }

    [Fact]
    public async Task A_read_only_post_is_retried_on_503_but_an_ordinary_post_is_not()
    {
        static Func<HttpRequestMessage, HttpResponseMessage> Fail() =>
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };

        var retried = new StubHandler(Fail(), Respond("{}"));
        await Client(retried).SendAsync(HttpMethod.Post, "/x", "{}", default, readOnly: true);
        Assert.Equal(2, retried.Urls.Count);

        var notRetried = new StubHandler(Fail(), Respond("{}"));
        await Assert.ThrowsAsync<PpacException>(() => Client(notRetried).SendAsync(HttpMethod.Post, "/x", "{}"));
        Assert.Single(notRetried.Urls);
    }
}