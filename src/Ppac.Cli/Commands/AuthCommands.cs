using System.CommandLine;
using System.Text.Json.Nodes;
using Ppac.Cli.Auth;
using Ppac.Core;
using Ppac.Core.Config;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

public static class AuthCommands
{
    public static Command Create()
    {
        var auth = new Command("auth", "Sign in and manage credentials");
        auth.Subcommands.Add(Login());
        auth.Subcommands.Add(Status());
        auth.Subcommands.Add(Logout());
        return auth;
    }

    private static Command Login()
    {
        var tenant = new Option<string>("--tenant") { Description = "Tenant ID (or PPAC_TENANT_ID)" };
        var client = new Option<string>("--client-id") { Description = "Client ID of your public-client app registration (or PPAC_CLIENT_ID)" };
        var deviceCode = new Option<bool>("--device-code") { Description = "Sign in with a device code instead of opening a browser" };

        var command = new Command("login", "Sign in as an admin (interactive). Other commands never prompt.");
        command.Options.Add(tenant);
        command.Options.Add(client);
        command.Options.Add(deviceCode);

        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var profile = store.Resolve(parse.GetValue(tenant), parse.GetValue(client), Environment.GetEnvironmentVariable);

            var result = await new MsalAuth(profile).LoginAsync(
                parse.GetValue(deviceCode), message => Console.Error.WriteLine(message), token);

            store.Save(profile with { Username = result.Account.Username });
            Console.Error.WriteLine($"Signed in as {result.Account.Username}");
            return ExitCodes.Ok;
        }, ct));

        return command;
    }

    private static Command Status()
    {
        var command = new Command("status", "Show the signed-in account. Exits 3 if not signed in.");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var profile = new ProfileStore().Resolve(null, null, Environment.GetEnvironmentVariable);
            var result = await new MsalAuth(profile).AcquireSilentAsync(token);

            var row = new JsonObject
            {
                ["tenantId"] = profile.TenantId,
                ["clientId"] = profile.ClientId,
                ["user"] = result.Account.Username,
                ["tokenExpiresOn"] = result.ExpiresOn.UtcDateTime.ToString("o"),
            };
            OutputWriter.Write(Console.Out, [row],
                [new("Tenant", "tenantId"), new("Client", "clientId"), new("User", "user"), new("TokenExpiresOn", "tokenExpiresOn")],
                format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Logout()
    {
        var command = new Command("logout", "Remove cached tokens for this app");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var profile = store.Resolve(null, null, Environment.GetEnvironmentVariable);
            await new MsalAuth(profile).LogoutAsync();
            store.Save(profile with { Username = null });
            Console.Error.WriteLine("Signed out.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}
