using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ppac.Core.Output;

/// <summary>
/// Writes one object (for "show" commands). json and jsonl give the full object, not an array;
/// table, csv and tsv give a Property/Value listing with nested fields as dotted names.
/// </summary>
public static class ObjectWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Write(TextWriter writer, JsonObject obj, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                writer.WriteLine(obj.ToJsonString(Indented));
                break;
            case OutputFormat.Jsonl:
                writer.WriteLine(obj.ToJsonString());
                break;
            default:
                OutputWriter.Write(writer, Properties(obj), Columns.Properties, format);
                break;
        }
    }

    /// <summary>Flattens an object into property/value rows, in the original order. Arrays stay as JSON text.</summary>
    public static IReadOnlyList<JsonObject> Properties(JsonObject obj)
    {
        var rows = new List<JsonObject>();
        Walk(rows, "", obj);
        return rows;
    }

    private static void Walk(List<JsonObject> rows, string prefix, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
                Walk(rows, prefix.Length == 0 ? key : prefix + "." + key, value);
            return;
        }

        rows.Add(new JsonObject { ["property"] = prefix, ["value"] = Text(node) });
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };
}