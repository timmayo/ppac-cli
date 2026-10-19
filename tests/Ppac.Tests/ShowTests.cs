using System.Text.Json.Nodes;
using Ppac.Core;
using Ppac.Core.Environments;
using Ppac.Core.Output;

namespace Ppac.Tests;

public class EnvironmentLookupTests
{
    private static JsonObject Env(string id, string name) =>
        (JsonObject)JsonNode.Parse($$"""{"id":"{{id}}","displayName":"{{name}}"}""")!;

    private static readonly List<JsonObject> Environments =
    [
        Env("id-1", "Dev"),
        Env("id-2", "Dev (old)"),
        Env("id-3", "Prod"),
        Env("id-4", "Shared"),
        Env("id-5", "shared"),
    ];

    [Fact]
    public void Finds_by_id()
    {
        Assert.Equal("Prod", OutputWriter.CellText(EnvironmentLookup.Find(Environments, "id-3"), "displayName"));
    }

    [Fact]
    public void Id_match_ignores_case_and_surrounding_spaces()
    {
        Assert.Equal("Prod", OutputWriter.CellText(EnvironmentLookup.Find(Environments, "  ID-3 "), "displayName"));
    }

    [Fact]
    public void Finds_by_exact_display_name_without_matching_prefixes()
    {
        var found = EnvironmentLookup.Find(Environments, "dev");
        Assert.Equal("id-1", OutputWriter.CellText(found, "id"));
    }

    [Fact]
    public void Ambiguous_display_name_is_a_usage_error_that_lists_ids()
    {
        var ex = Assert.Throws<PpacException>(() => EnvironmentLookup.Find(Environments, "SHARED"));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("id-4", ex.Message);
        Assert.Contains("id-5", ex.Message);
    }

    [Fact]
    public void Missing_environment_exits_not_found()
    {
        var ex = Assert.Throws<PpacException>(() => EnvironmentLookup.Find(Environments, "nope"));
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
    }
}

public class ObjectWriterTests
{
    private static JsonObject Sample() => (JsonObject)JsonNode.Parse("""
        {"id":"e1","displayName":"Dev","createdBy":{"id":"u1","type":"User"},"tags":["a","b"],"gone":null}
        """)!;

    private static string Render(OutputFormat format)
    {
        var writer = new StringWriter { NewLine = "\n" };
        ObjectWriter.Write(writer, Sample(), format);
        return writer.ToString();
    }

    [Fact]
    public void Properties_flatten_nested_objects_in_order()
    {
        var rows = ObjectWriter.Properties(Sample());
        var pairs = rows.Select(r => OutputWriter.CellText(r, "property") + "=" + OutputWriter.CellText(r, "value")).ToList();
        Assert.Equal("id=e1", pairs[0]);
        Assert.Equal("displayName=Dev", pairs[1]);
        Assert.Equal("createdBy.id=u1", pairs[2]);
        Assert.Equal("createdBy.type=User", pairs[3]);
        Assert.Equal("tags=[\"a\",\"b\"]", pairs[4]);
        Assert.Equal("gone=", pairs[5]);
    }

    [Fact]
    public void Json_is_one_object_not_an_array()
    {
        var node = JsonNode.Parse(Render(OutputFormat.Json));
        Assert.True(node is JsonObject);
        Assert.Equal("e1", (string)node!["id"]!);
    }

    [Fact]
    public void Jsonl_is_a_single_line()
    {
        Assert.Single(Render(OutputFormat.Jsonl).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Table_has_property_and_value_headers()
    {
        var first = Render(OutputFormat.Table).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("Property", first);
        Assert.Contains("Value", first);
    }
}