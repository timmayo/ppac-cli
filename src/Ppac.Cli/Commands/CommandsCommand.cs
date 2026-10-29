using System.Reflection;
using System.Text.Json.Nodes;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

/// <summary>'ppac commands': lists the command registry for people, scripts and AI agents.</summary>
public static class CommandsCommand
{
    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "commands" },
            Description = "List all commands with their class and verification status.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Api = "Local command registry",
            Options = new[] { JsonOutput.Option },
            Handler = (ctx, _) => Task.FromResult(Run(ctx))
        };
    }

    private static int Run(CommandContext ctx)
    {
        var specs = CommandRegistry.All.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        if (ctx.GetBool(JsonOutput.OptionName))
        {
            JsonOutput.Write(BuildJson(specs));
            return ExitCodes.Success;
        }

        Console.WriteLine($"{"Command",-24}{"Class",-13}{"Status",-16}{"MCP",-5}{"Description"}");
        foreach (var s in specs)
        {
            Console.WriteLine(
                $"{s.Name,-24}{ClassName(s.Class),-13}{StatusName(s.Status),-16}{(s.McpEligible ? "yes" : "no"),-5}{s.Description}");
        }
        return ExitCodes.Success;
    }

    private static JsonObject BuildJson(IReadOnlyList<CommandSpec> specs)
    {
        var version = typeof(CommandsCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        var commands = new JsonArray();
        foreach (var s in specs)
        {
            var options = new JsonArray();
            foreach (var o in s.Options)
            {
                options.Add(new JsonObject
                {
                    ["name"] = o.Name,
                    ["aliases"] = new JsonArray(o.Aliases.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                    ["type"] = o.Type.ToString().ToLowerInvariant(),
                    ["required"] = o.Required,
                    ["description"] = o.Description
                });
            }

            commands.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["path"] = new JsonArray(s.Path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["description"] = s.Description,
                ["class"] = ClassName(s.Class),
                ["status"] = StatusName(s.Status),
                ["preview"] = s.Preview,
                ["interactive"] = s.Interactive,
                ["mcpEligible"] = s.McpEligible,
                ["api"] = s.Api,
                ["permission"] = s.Permission,
                ["options"] = options
            });
        }

        return new JsonObject
        {
            ["tool"] = "ppac",
            ["version"] = version,
            ["exitCodes"] = new JsonObject
            {
                [ExitCodes.Success.ToString()] = "success",
                [ExitCodes.Failure.ToString()] = "the command ran but failed",
                [ExitCodes.Usage.ToString()] = "invalid command line"
            },
            ["commands"] = commands
        };
    }

    private static string ClassName(CommandClass value) => value.ToString().ToLowerInvariant();

    private static string StatusName(VerificationStatus value) => value switch
    {
        VerificationStatus.Verified => "verified",
        VerificationStatus.OperationOnly => "operation-only",
        _ => "unverified"
    };
}