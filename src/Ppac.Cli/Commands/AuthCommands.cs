using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;

namespace Ppac.Cli.Commands;

public static class AuthCommands
{
    // PAC CLI limits profile names to 1-30 characters; mirror that.
    private const int MaxProfileNameLength = 30;

    public static IEnumerable<CommandSpec> Specs()
    {
        yield return new CommandSpec
        {
            Path = new[] { "auth", "create" },
            Description = "Create an authentication profile and sign in.",
            Class = CommandClass.Write,
            Status = VerificationStatus.Verified,
            Interactive = true,
            Api = "Microsoft Entra ID device code flow (MSAL.NET); writes the local profile store",
            Options = new[]
            {
                new OptionSpec("--name", new[] { "-n" }, OptionType.String,
                    "Profile name (1-30 characters). Defaults to the signed-in user's name."),
                new OptionSpec("--tenant", new[] { "-t" }, OptionType.String,
                    "Directory (tenant) ID or domain, for example contoso.onmicrosoft.com.", Required: true),
                new OptionSpec("--applicationId", new[] { "-id" }, OptionType.String,
                    "Application (client) ID of the Entra app registration.", Required: true),
                new OptionSpec("--deviceCode", new[] { "-dc" }, OptionType.Bool,
                    "Sign in with the device code flow."),
                JsonOutput.Option
            },
            Handler = CreateAsync
        };

        yield return new CommandSpec
        {
            Path = new[] { "auth", "list" },
            Description = "List authentication profiles.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Api = "Local profile store",
            Options = new[] { JsonOutput.Option },
            Handler = (ctx, _) => Task.FromResult(List(ctx))
        };

        yield return new CommandSpec
        {
            Path = new[] { "auth", "who" },
            Description = "Show the active authentication profile.",
            Class = CommandClass.Read,
            Status = VerificationStatus.Verified,
            Api = "Local profile store",
            Options = new[] { JsonOutput.Option },
            Handler = (ctx, _) => Task.FromResult(Who(ctx))
        };
    }

    private static async Task<int> CreateAsync(CommandContext ctx, CancellationToken cancellationToken)
    {
        var json = ctx.GetBool(JsonOutput.OptionName);

        if (!ctx.GetBool("--deviceCode"))
        {
            Console.Error.WriteLine("Only --deviceCode sign-in is supported in this version.");
            return ExitCodes.Usage;
        }

        var requestedName = ctx.GetString("--name");
        if (requestedName is not null && (requestedName.Length is 0 or > MaxProfileNameLength))
        {
            Console.Error.WriteLine($"--name must be 1-{MaxProfileNameLength} characters.");
            return ExitCodes.Usage;
        }

        var tenant = ctx.GetString("--tenant")!;
        var clientId = ctx.GetString("--applicationId")!;
        var cloud = CloudConfig.Commercial;

        // In --json mode keep stdout clean: the sign-in instructions go to stderr.
        Action<string> showMessage = message =>
        {
            if (json)
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        };

        try
        {
            var app = await TokenProvider.CreateAppAsync(tenant, clientId, cloud);
            var result = await TokenProvider.SignInWithDeviceCodeAsync(app, cloud, showMessage, cancellationToken);

            var profileName = requestedName ?? Truncate(result.Account.Username, MaxProfileNameLength);
            ProfileStore.Upsert(new Profile(
                Name: profileName,
                TenantId: tenant,
                ClientId: clientId,
                Cloud: cloud.Name,
                Username: result.Account.Username,
                HomeAccountId: result.Account.HomeAccountId.Identifier));

            if (json)
            {
                JsonOutput.Write(new JsonObject
                {
                    ["profile"] = profileName,
                    ["user"] = result.Account.Username,
                    ["tenantId"] = tenant,
                    ["clientId"] = clientId,
                    ["cloud"] = cloud.Name
                });
            }
            else
            {
                Console.WriteLine($"Signed in as {result.Account.Username}.");
                Console.WriteLine($"Profile '{profileName}' created and selected.");
            }
            return ExitCodes.Success;
        }
        catch (MsalServiceException ex)
        {
            Console.Error.WriteLine($"Sign-in failed ({ex.ErrorCode}): {ex.Message}");
            return ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Sign-in cancelled.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sign-in failed: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static int List(CommandContext ctx)
    {
        var file = ProfileStore.Load();

        if (ctx.GetBool(JsonOutput.OptionName))
        {
            var profiles = new JsonArray();
            for (var i = 0; i < file.Profiles.Count; i++)
            {
                var p = file.Profiles[i];
                profiles.Add(new JsonObject
                {
                    ["index"] = i + 1,
                    ["active"] = IsActive(p, file),
                    ["name"] = p.Name,
                    ["user"] = p.Username,
                    ["tenantId"] = p.TenantId,
                    ["clientId"] = p.ClientId,
                    ["cloud"] = p.Cloud
                });
            }
            JsonOutput.Write(new JsonObject { ["profiles"] = profiles });
            return ExitCodes.Success;
        }

        if (file.Profiles.Count == 0)
        {
            Console.WriteLine("No profiles. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.");
            return ExitCodes.Success;
        }

        Console.WriteLine($"{"Index",-7}{"",-3}{"Name",-32}{"User",-35}{"Tenant"}");
        for (var i = 0; i < file.Profiles.Count; i++)
        {
            var p = file.Profiles[i];
            var active = IsActive(p, file) ? "*" : "";
            Console.WriteLine($"{i + 1,-7}{active,-3}{p.Name,-32}{p.Username,-35}{p.TenantId}");
        }
        return ExitCodes.Success;
    }

    private static int Who(CommandContext ctx)
    {
        var active = ProfileStore.GetActive();
        if (active is null)
        {
            Console.Error.WriteLine("No active profile. Run 'ppac auth create --deviceCode ...'.");
            return ExitCodes.Failure;
        }

        if (ctx.GetBool(JsonOutput.OptionName))
        {
            JsonOutput.Write(new JsonObject
            {
                ["profile"] = active.Name,
                ["user"] = active.Username,
                ["tenantId"] = active.TenantId,
                ["clientId"] = active.ClientId,
                ["cloud"] = active.Cloud
            });
            return ExitCodes.Success;
        }

        Console.WriteLine($"Profile:   {active.Name}");
        Console.WriteLine($"User:      {active.Username}");
        Console.WriteLine($"Tenant:    {active.TenantId}");
        Console.WriteLine($"Client ID: {active.ClientId}");
        Console.WriteLine($"Cloud:     {active.Cloud}");
        return ExitCodes.Success;
    }

    private static bool IsActive(Profile profile, ProfileFile file) =>
        string.Equals(profile.Name, file.Active, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}