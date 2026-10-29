using System.CommandLine;
using Microsoft.Identity.Client;
using Ppac.Cli.Auth;
using Ppac.Cli.Config;

namespace Ppac.Cli.Commands;

public static class AuthCommands
{
    // PAC CLI limits profile names to 1-30 characters; mirror that.
    private const int MaxProfileNameLength = 30;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public static Command Build()
    {
        var auth = new Command("auth", "Manage authentication profiles.");
        auth.Subcommands.Add(BuildCreate());
        auth.Subcommands.Add(BuildList());
        auth.Subcommands.Add(BuildWho());
        return auth;
    }

    private static Command BuildCreate()
    {
        var name = new Option<string?>("--name", "-n")
        {
            Description = "Profile name. Defaults to the signed-in user's name."
        };
        var tenant = new Option<string>("--tenant", "-t")
        {
            Description = "Directory (tenant) ID or domain, for example contoso.onmicrosoft.com.",
            Required = true
        };
        var applicationId = new Option<string>("--applicationId", "-id")
        {
            Description = "Application (client) ID of the Entra app registration.",
            Required = true
        };
        var deviceCode = new Option<bool>("--deviceCode", "-dc")
        {
            Description = "Sign in with the device code flow."
        };

        var create = new Command("create", "Create an authentication profile and sign in.")
        {
            name, tenant, applicationId, deviceCode
        };

        create.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!parseResult.GetValue(deviceCode))
            {
                Console.Error.WriteLine("Only --deviceCode sign-in is supported in this version.");
                return 2;
            }

            var requestedName = parseResult.GetValue(name);
            if (requestedName is not null && (requestedName.Length is 0 or > MaxProfileNameLength))
            {
                Console.Error.WriteLine($"--name must be 1-{MaxProfileNameLength} characters.");
                return 2;
            }

            var tenantValue = parseResult.GetValue(tenant)!;
            var clientValue = parseResult.GetValue(applicationId)!;
            var cloud = CloudConfig.Commercial;

            try
            {
                var app = await TokenProvider.CreateAppAsync(tenantValue, clientValue, cloud);
                var result = await TokenProvider.SignInWithDeviceCodeAsync(app, cloud, cancellationToken);

                var profileName = parseResult.GetValue(name) ?? Truncate(result.Account.Username, MaxProfileNameLength);
                ProfileStore.Upsert(new Profile(
                    Name: profileName,
                    TenantId: tenantValue,
                    ClientId: clientValue,
                    Cloud: cloud.Name,
                    Username: result.Account.Username,
                    HomeAccountId: result.Account.HomeAccountId.Identifier));

                Console.WriteLine($"Signed in as {result.Account.Username}.");
                Console.WriteLine($"Profile '{profileName}' created and selected.");
                return 0;
            }
            catch (MsalServiceException ex)
            {
                Console.Error.WriteLine($"Sign-in failed ({ex.ErrorCode}): {ex.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Sign-in cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Sign-in failed: {ex.Message}");
                return 1;
            }
        });

        return create;
    }

    private static Command BuildList()
    {
        var list = new Command("list", "List authentication profiles.");

        list.SetAction(_ =>
        {
            var file = ProfileStore.Load();
            if (file.Profiles.Count == 0)
            {
                Console.WriteLine("No profiles. Run 'ppac auth create --deviceCode --tenant <id> --applicationId <id>'.");
                return 0;
            }

            Console.WriteLine($"{"Index",-7}{"",-3}{"Name",-32}{"User",-35}{"Tenant"}");
            for (var i = 0; i < file.Profiles.Count; i++)
            {
                var p = file.Profiles[i];
                var active = string.Equals(p.Name, file.Active, StringComparison.OrdinalIgnoreCase) ? "*" : "";
                Console.WriteLine($"{i + 1,-7}{active,-3}{p.Name,-32}{p.Username,-35}{p.TenantId}");
            }
            return 0;
        });

        return list;
    }

    private static Command BuildWho()
    {
        var who = new Command("who", "Show the active authentication profile.");

        who.SetAction(_ =>
        {
            var active = ProfileStore.GetActive();
            if (active is null)
            {
                Console.Error.WriteLine("No active profile. Run 'ppac auth create --deviceCode ...'.");
                return 1;
            }

            Console.WriteLine($"Profile:   {active.Name}");
            Console.WriteLine($"User:      {active.Username}");
            Console.WriteLine($"Tenant:    {active.TenantId}");
            Console.WriteLine($"Client ID: {active.ClientId}");
            Console.WriteLine($"Cloud:     {active.Cloud}");
            return 0;
        });

        return who;
    }
}