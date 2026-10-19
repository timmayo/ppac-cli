using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Output;

namespace Ppac.Tests;

public class GroupTests
{
    // Shape copied from a real GET /environmentmanagement/environmentGroups response (2022-03-01-preview), values anonymized.
    private const string GroupJson = """
        {
          "id": "00000000-0000-0000-0000-0000000000g1",
          "displayName": "Green Zone",
          "description": "Low-risk environments",
          "createdTime": "2025-12-03T16:31:06.7798965Z",
          "createdBy": { "id": "00000000-0000-0000-0000-0000000000bb", "type": "User" }
        }
        """;

    private static JsonObject Group(string id, string name) =>
        (JsonObject)JsonNode.Parse($$"""{"id":"{{id}}","displayName":"{{name}}"}""")!;

    [Fact]
    public void Group_columns_read_real_response_fields()
    {
        var row = (JsonObject)JsonNode.Parse(GroupJson)!;
        var values = Columns.Groups.ToDictionary(c => c.Header, c => OutputWriter.CellText(row, c.Path));

        Assert.Equal("Green Zone", values["DisplayName"]);
        Assert.Equal("Low-risk environments", values["Description"]);
        Assert.Equal("2025-12-03T16:31:06.7798965Z", values["Created"]);
        Assert.Equal("00000000-0000-0000-0000-0000000000g1", values["Id"]);
    }

    [Fact]
    public void Lookup_finds_a_group_by_name_or_id()
    {
        var groups = new List<JsonObject> { Group("g-1", "Green Zone"), Group("g-2", "Red Zone") };

        Assert.Equal("g-2", OutputWriter.CellText(ResourceLookup.Find(groups, "red zone", "Environment group"), "id"));
        Assert.Equal("Green Zone", OutputWriter.CellText(ResourceLookup.Find(groups, "g-1", "Environment group"), "displayName"));
    }

    [Fact]
    public void Lookup_not_found_names_the_resource_and_exits_4()
    {
        var groups = new List<JsonObject> { Group("g-1", "Green Zone") };
        var ex = Assert.Throws<PpacException>(() => ResourceLookup.Find(groups, "Blue", "Environment group"));
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Contains("Environment group 'Blue' not found", ex.Message);
    }

    [Fact]
    public void Lookup_ambiguous_name_exits_2_and_lists_ids()
    {
        var groups = new List<JsonObject> { Group("g-1", "Zone"), Group("g-2", "zone") };
        var ex = Assert.Throws<PpacException>(() => ResourceLookup.Find(groups, "ZONE", "Environment group"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("environment groups", ex.Message);
        Assert.Contains("g-1", ex.Message);
        Assert.Contains("g-2", ex.Message);
    }
}