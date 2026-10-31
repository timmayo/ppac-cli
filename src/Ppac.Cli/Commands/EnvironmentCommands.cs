using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

public static class EnvironmentCommands
{
    private const string ReadPermission = "EnvironmentManagement.Environments.Read";

    // How many environments 'environment get' lists when --environment is omitted.
    private const int DefaultListCount = 10;

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "environment", "list" },
            Description = "List environments in the tenant.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Preview = true,
            Api = $"GET /environmentmanagement/environments?api-version={EnvironmentApi.ApiVersion}",
            Permission = ReadPermission,
            Options = new[]
            {
                new OptionSpec("--filter", new[] { "-f" }, OptionType.String,
                    "OData filter. Supported properties: dataverseId, type, geo, state, environmentGroupId, domainName. Examples: \"type eq 'Sandbox'\", \"state eq 'Enabled'\"."),
                new OptionSpec("--top", Array.Empty<string>(), OptionType.Int,
                    "Maximum number of environments to return."),
                JsonOutput.Option
            },
            Handler = ListAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "environment", "get" },
            Description = "Show the details of one environment, or the first 10 environments when --environment is omitted.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Preview = true,
            Api = $"GET /environmentmanagement/environments/{{environmentId}}?api-version={EnvironmentApi.ApiVersion}",
            Permission = ReadPermission,
            Options = new[]
            {
                new OptionSpec("--environment", Array.Empty<string>(), OptionType.String,
                    "Environment ID, as shown by 'ppac environment list'. When omitted, the first 10 environments are listed."),
                new OptionSpec("--select", Array.Empty<string>(), OptionType.String,
                    "Comma-separated list of properties to return, for example \"displayName,state,url\". Only with --environment."),
                JsonOutput.Option
            },
            Handler = GetAsync
        };
    }

    private static Task<int> ListAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var top = ctx.GetInt("--top");
        if (top is not null && top < 1)
        {
            Console.Error.WriteLine("--top must be 1 or greater.");
            return Task.FromResult(ExitCodes.Usage);
        }

        return RunAsync(async (api, cloud) =>
        {
            var environments = await EnvironmentApi.ListAsync(
                api, cloud, ctx.GetString("--filter"), top, cancellationToken);

            if (ctx.GetBool(JsonOutput.OptionName))
            {
                JsonOutput.Write(new JsonObject { ["value"] = environments });
            }
            else
            {
                WriteTable(environments);
            }
            return ExitCodes.Success;
        }, cancellationToken);
    }

    private static Task<int> GetAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var environmentId = ctx.GetString("--environment");
        var select = ctx.GetString("--select");

        // No --environment: fall back to the first DefaultListCount environments.
        if (environmentId is null)
        {
            if (select is not null)
            {
                Console.Error.WriteLine("--select can only be used together with --environment.");
                return Task.FromResult(ExitCodes.Usage);
            }

            return RunAsync(async (api, cloud) =>
            {
                var environments = await EnvironmentApi.ListAsync(
                    api, cloud, null, DefaultListCount, cancellationToken);

                if (ctx.GetBool(JsonOutput.OptionName))
                {
                    JsonOutput.Write(new JsonObject { ["value"] = environments });
                }
                else
                {
                    WriteTable(environments);
                    Console.Error.WriteLine(
                        $"Showing up to {DefaultListCount} environments. Use --environment <id> for one environment's details, or 'ppac environment list' for all.");
                }
                return ExitCodes.Success;
            }, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(environmentId))
        {
            Console.Error.WriteLine("--environment must not be empty.");
            return Task.FromResult(ExitCodes.Usage);
        }

        return RunAsync(async (api, cloud) =>
        {
            var environment = await EnvironmentApi.GetAsync(
                api, cloud, environmentId.Trim(), select, cancellationToken);

            if (ctx.GetBool(JsonOutput.OptionName))
            {
                JsonOutput.Write(environment);
            }
            else
            {
                WriteDetails(environment);
            }
            return ExitCodes.Success;
        }, cancellationToken);
    }

    private static Task<int> RunAsync(
        Func<PowerPlatformApi, CloudConfig, Task<int>> call, CancellationToken cancellationToken) =>
        ApiCommandRunner.RunAsync(
            call,
            forbiddenHint:
                $"Check that the app registration has the delegated permission {ReadPermission} " +
                "with admin consent, and that the signed-in user has a Power Platform admin role.",
            notFoundHint: "Check the environment ID. 'ppac environment list' shows the IDs.",
            cancellationToken);

    private static void WriteTable(JsonArray environments)
    {
        if (environments.Count == 0)
        {
            Console.WriteLine("No environments found.");
            return;
        }

        var rows = environments
            .Select(e => new
            {
                Name = Str(e, "displayName") ?? "",
                Type = Str(e, "type") ?? "",
                State = Str(e, "state") ?? "",
                Region = Str(e, "azureRegion") ?? Str(e, "geo") ?? "",
                Id = Str(e, "id") ?? ""
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"{"Name",-40}{"Type",-14}{"State",-12}{"Region",-16}{"Environment ID"}");
        foreach (var r in rows)
        {
            var name = r.Name.Length <= 38 ? r.Name : r.Name[..37] + "…";
            Console.WriteLine($"{name,-40}{r.Type,-14}{r.State,-12}{r.Region,-16}{r.Id}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} environment(s).");
    }

    // Prints the commonly needed properties that are present. --json returns everything.
    private static void WriteDetails(JsonNode environment)
    {
        var fields = new (string Label, string Property)[]
        {
            ("Name", "displayName"),
            ("Environment ID", "id"),
            ("Type", "type"),
            ("State", "state"),
            ("Region", "azureRegion"),
            ("Geo", "geo"),
            ("Admin mode", "adminMode"),
            ("Protection level", "protectionLevel"),
            ("Dataverse ID", "dataverseId"),
            ("Domain name", "domainName"),
            ("URL", "url"),
            ("Version", "version"),
            ("Environment group ID", "environmentGroupId"),
            ("Security group ID", "securityGroupId"),
            ("Connected group ID", "connectedGroupId"),
            ("Created", "createdDateTime"),
            ("Tenant ID", "tenantId")
        };

        foreach (var (label, property) in fields)
        {
            var value = Str(environment, property);
            if (!string.IsNullOrEmpty(value))
            {
                Console.WriteLine($"{label + ":",-23}{value}");
            }
        }

        var createdBy = Str(environment["createdBy"], "id");
        if (!string.IsNullOrEmpty(createdBy))
        {
            Console.WriteLine($"{"Created by:",-23}{createdBy}");
        }
    }

    // Reads a property as text. Date values arrive as JSON strings, so this covers them too.
    private static string? Str(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}