using System.Text.Json;
using System.Text.Json.Nodes;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Output;

/// <summary>Shared --json option and writer, so every command behaves the same.</summary>
public static class JsonOutput
{
    public const string OptionName = "--json";

    public static readonly OptionSpec Option = new(
        OptionName, Array.Empty<string>(), OptionType.Bool, "Write the result as JSON.");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(JsonNode node) => Console.WriteLine(node.ToJsonString(Options));
}