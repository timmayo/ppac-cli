using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ppac.Core.Output;

public enum OutputFormat
{
    Table,
    Json,
    Jsonl,
    Csv,
    Tsv,
}

/// <summary>A displayed column: header text and a dotted path into each row object.</summary>
public sealed record Column(string Header, string Path);

/// <summary>
/// Writes rows in the requested format. Data goes to the supplied writer (stdout);
/// callers send everything else to stderr. json and jsonl always contain the full
/// objects; the other formats contain only the given columns.
/// </summary>
public static class OutputWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static bool TryParseFormat(string? text, out OutputFormat format)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "table": format = OutputFormat.Table; return true;
            case "json": format = OutputFormat.Json; return true;
            case "jsonl": format = OutputFormat.Jsonl; return true;
            case "csv": format = OutputFormat.Csv; return true;
            case "tsv": format = OutputFormat.Tsv; return true;
            default: format = OutputFormat.Table; return false;
        }
    }

    public static OutputFormat ParseFormatOrThrow(string? text)
    {
        if (TryParseFormat(text, out var format)) return format;
        throw new PpacException(ExitCodes.Usage,
            $"Unknown output format '{text}'. Use one of: table, json, jsonl, csv, tsv.");
    }

    public static void Write(TextWriter writer, IReadOnlyList<JsonObject> rows, IReadOnlyList<Column> columns, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                var array = new JsonArray(rows.Select(r => r.DeepClone()).ToArray());
                writer.WriteLine(array.ToJsonString(Indented));
                break;
            case OutputFormat.Jsonl:
                foreach (var row in rows) writer.WriteLine(row.ToJsonString());
                break;
            case OutputFormat.Csv:
                WriteDelimited(writer, rows, columns, ',', Csv);
                break;
            case OutputFormat.Tsv:
                WriteDelimited(writer, rows, columns, '\t', Tsv);
                break;
            default:
                WriteTable(writer, rows, columns);
                break;
        }
    }

    /// <summary>Reads a dotted path (for example "properties.displayName") from a row.</summary>
    public static string CellText(JsonObject row, string path)
    {
        JsonNode? node = row;
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj ? obj[segment] : null;
            if (node is null) return "";
        }

        return node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }

    private static void WriteTable(TextWriter writer, IReadOnlyList<JsonObject> rows, IReadOnlyList<Column> columns)
    {
        var cells = rows.Select(r => columns.Select(c => Sanitize(CellText(r, c.Path))).ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Header.Length, cells.Count == 0 ? 0 : cells.Max(row => row[i].Length))).ToArray();

        writer.WriteLine(Line(columns.Select(c => c.Header).ToArray(), widths));
        writer.WriteLine(Line(widths.Select(w => new string('-', w)).ToArray(), widths));
        foreach (var row in cells) writer.WriteLine(Line(row, widths));
    }

    private static string Line(string[] values, int[] widths)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append("  ");
            sb.Append(values[i].PadRight(widths[i]));
        }

        return sb.ToString().TrimEnd();
    }

    private static void WriteDelimited(TextWriter writer, IReadOnlyList<JsonObject> rows, IReadOnlyList<Column> columns,
        char separator, Func<string, string> escape)
    {
        writer.WriteLine(string.Join(separator, columns.Select(c => escape(c.Header))));
        foreach (var row in rows)
            writer.WriteLine(string.Join(separator, columns.Select(c => escape(CellText(row, c.Path)))));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Tsv(string value) => Sanitize(value);

    private static string Sanitize(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
