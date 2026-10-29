using System.CommandLine;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;

namespace Ppac.Cli.Commands;

public static class EnvironmentCommands
{
    private static readonly JsonSerializerOptions JsonOutput = new() { WriteIndented = true };

    public static Command Build()
    {
        var environment = new Command("environment", "Manage Power Platform environments.");
        environment.Subcommands.Add(BuildList());
        return environment;
    }

    private static Command BuildList()
    {
        var filter = new Option<string?>("--filter", "-f")
        {
            Description = "OData filter. Supported properties: dataverseId, type, geo, state, environmentGroupId, domainName. Examples: \"type eq 'Sandbox'\", \"state eq 'Enabled'\"."
        };
        var top = new Option<int?>("--top")
        {
            Description = "Maximum number of environments to return."
        };
        var json = new Option<bool>("--json")
        {
            Description = "Write the result as JSON."
        };

        var list = new Command("list", "List environments in the tenant.")
        {
            filter, top, json
        };

        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var topValue = parseResult.GetValue(top);
            if (topValue is not null && topValue < 1)
            {
                Console.Error.WriteLine("--top must be 1 or greater.");
                return 2;
            }

            var profile = ProfileStore.GetActive();
            if (profile is null)
            {
                Console.Error.WriteLine("No active profile. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.");
                return 1;
            }

            var cloud = CloudConfig.Get(profile.Cloud);

            string token;
            try
            {
                token = await TokenProvider.GetAccessTokenAsync(profile, cloud, cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                Console.Error.WriteLine("Your sign-in has expired or is missing. Run 'ppac auth create --deviceCode ...' again.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not get an access token: {ex.Message}");
                return 1;
            }

            try
            {
                using var http = new HttpClient();
                var api = new PowerPlatformApi(http, token);
                var environments = await EnvironmentApi.ListAsync(
                    api, cloud, parseResult.GetValue(filter), topValue, cancellationToken);

                if (parseResult.GetValue(json))
                {
                    var wrapper = new JsonObject { ["value"] = environments };
                    Console.WriteLine(wrapper.ToJsonString(JsonOutput));
                }
                else
                {
                    WriteTable(environments);
                }
                return 0;
            }
            catch (ApiException ex)
            {
                Console.Error.WriteLine($"The request failed: {(int)ex.StatusCode} {ex.StatusCode}. {ex.Message}");
                if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    Console.Error.WriteLine(
                        "Check that the app registration has the delegated permission " +
                        "EnvironmentManagement.Environments.Read with admin consent, and that the " +
                        "signed-in user has a Power Platform admin role.");
                }
                return 1;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Could not reach the Power Platform API: {ex.Message}");
                return 1;
            }
        });

        return list;
    }

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

    private static string? Str(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}