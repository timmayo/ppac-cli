using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Ppac.Cli.Api;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

public static class EnvironmentCommands
{
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
            Permission = "EnvironmentManagement.Environments.Read",
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
    }

    private static async Task<int> ListAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var top = ctx.GetInt("--top");
        if (top is not null && top < 1)
        {
            Console.Error.WriteLine("--top must be 1 or greater.");
            return ExitCodes.Usage;
        }

        var profile = ProfileStore.GetActive();
        if (profile is null)
        {
            Console.Error.WriteLine("No active profile. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.");
            return ExitCodes.Failure;
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
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not get an access token: {ex.Message}");
            return ExitCodes.Failure;
        }

        try
        {
            using var http = new HttpClient();
            var api = new PowerPlatformApi(http, token);
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
            return ExitCodes.Failure;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not reach the Power Platform API: {ex.Message}");
            return ExitCodes.Failure;
        }
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