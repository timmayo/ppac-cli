using System.CommandLine;
using System.Text.Json.Nodes;
using Ppac.Cli.Auth;
using Ppac.Core;
using Ppac.Core.Config;
using Ppac.Core.Output;

namespace Ppac.Cli.Commands;

/// <summary>Authentication profiles, modelled on "pac auth": create, list, select, delete, clear, name, who, token.</summary>
public static class AuthCommands
{
    private static readonly Column[] ListColumns =
    [
        new("Index", "index"), new("Active", "active"), new("Name", "name"), new("Method", "method"),
        new("Tenant", "tenantId"), new("Client", "clientId"), new("User", "user"),
    ];

    public static Command Create()
    {
        var auth = new Command("auth", "Sign in and manage authentication profiles");
        auth.Subcommands.Add(CreateProfile("create", "Create a profile and sign in. Re-run with the same --name to sign in again."));
        auth.Subcommands.Add(CreateProfile("login", "Same as create"));
        auth.Subcommands.Add(List());
        auth.Subcommands.Add(Select());
        auth.Subcommands.Add(Delete());
        auth.Subcommands.Add(Clear());
        auth.Subcommands.Add(Name());
        auth.Subcommands.Add(Who("who", "Show the active profile and the signed-in account. Exits 3 if not signed in."));
        auth.Subcommands.Add(Who("status", "Same as who"));
        auth.Subcommands.Add(Token());
        auth.Subcommands.Add(Logout());
        return auth;
    }

    // Options are created per command: the same Option instance must not be added to two commands.
    private static Option<string> NameOption(string description) => new("--name", "-n") { Description = description };

    private static Option<int> IndexOption(string description) => new("--index", "-i") { Description = description };

    private static Command CreateProfile(string commandName, string description)
    {
        var name = NameOption($"Profile name, 1 to {ProfileStore.MaxNameLength} characters (default: {ProfileStore.DefaultName})");
        var tenant = new Option<string>("--tenant", "-t") { Description = "Tenant ID (or PPAC_TENANT_ID)" };
        var client = new Option<string>("--client-id", "--application-id") { Description = "Client ID of your public-client app registration (or PPAC_CLIENT_ID)" };
        var deviceCode = new Option<bool>("--device-code") { Description = "Sign in with a device code instead of opening a browser" };

        var command = new Command(commandName, description);
        command.Options.Add(name);
        command.Options.Add(tenant);
        command.Options.Add(client);
        command.Options.Add(deviceCode);

        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var profile = store.ResolveForCreate(
                parse.GetValue(name), parse.GetValue(tenant), parse.GetValue(client), Environment.GetEnvironmentVariable);

            var result = await new MsalAuth(profile).LoginAsync(
                parse.GetValue(deviceCode), message => Console.Error.WriteLine(message), token);

            store.Upsert(profile with { Username = result.Account.Username }, makeActive: true);
            Console.Error.WriteLine($"Signed in as {result.Account.Username}. Profile '{profile.Name}' is now active.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command List()
    {
        var command = new Command("list", "List the profiles stored on this computer. * marks the active one.");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, token =>
        {
            var format = GlobalOptions.Format(parse);
            var file = new ProfileStore().LoadAll();

            var rows = new List<JsonObject>();
            for (var i = 0; i < file.Profiles.Count; i++)
            {
                var p = file.Profiles[i];
                var active = string.Equals(p.Name, file.Active, StringComparison.OrdinalIgnoreCase);
                rows.Add(new JsonObject
                {
                    ["index"] = i + 1,
                    ["active"] = active ? "*" : "",
                    ["name"] = p.Name,
                    ["method"] = p.Method,
                    ["tenantId"] = p.TenantId,
                    ["clientId"] = p.ClientId,
                    ["user"] = p.Username ?? "",
                });
            }

            OutputWriter.Write(Console.Out, rows, ListColumns, format);
            return Task.FromResult(ExitCodes.Ok);
        }, ct));
        return command;
    }

    private static Command Select()
    {
        var index = IndexOption("Index of the profile to make active (1 or more)");
        var name = NameOption("Name of the profile to make active");

        var command = new Command("select", "Select which profile is active");
        command.Options.Add(index);
        command.Options.Add(name);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, token =>
        {
            var store = new ProfileStore();
            store.Select(parse.GetValue(name), parse.GetValue(index));
            Console.Error.WriteLine($"Profile '{store.Load()!.Name}' is now active.");
            return Task.FromResult(ExitCodes.Ok);
        }, ct));
        return command;
    }

    private static Command Delete()
    {
        var index = IndexOption("Index of the profile to delete (1 or more)");
        var name = NameOption("Name of the profile to delete");

        var command = new Command("delete", "Delete a profile and its cached sign-in");
        command.Options.Add(index);
        command.Options.Add(name);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var found = ProfileStore.Find(store.LoadAll(), parse.GetValue(name), parse.GetValue(index));
            var wasActive = string.Equals(store.Load()?.Name, found.Name, StringComparison.OrdinalIgnoreCase);

            var sharedWith = ProfileStore.OtherProfileUsingAccount(store.LoadAll(), found);
            if (sharedWith is null) await new MsalAuth(found).LogoutAsync();
            store.Delete(found.Name, 0);

            Console.Error.WriteLine($"Deleted profile '{found.Name}'.");
            if (sharedWith is not null)
                Console.Error.WriteLine($"The cached sign-in was kept because profile '{sharedWith.Name}' uses the same account.");
            if (wasActive) Console.Error.WriteLine("No profile is active now. Run: ppac auth select");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Clear()
    {
        var yes = WriteHelpers.YesOption();

        var command = new Command("clear", "Delete every profile and cached sign-in on this computer");
        command.Options.Add(yes);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var file = store.LoadAll();

            WriteHelpers.CreateConfirmation().Require(
                $"delete all {file.Profiles.Count} authentication profile(s) on this computer", "clear", parse.GetValue(yes));

            foreach (var profile in file.Profiles)
                await new MsalAuth(profile).LogoutAsync();
            store.Clear();

            Console.Error.WriteLine("All profiles deleted.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Name()
    {
        var index = IndexOption("Index of the profile to name or rename (1 or more)");
        index.Required = true;
        var name = NameOption($"New name, 1 to {ProfileStore.MaxNameLength} characters");
        name.Required = true;

        var command = new Command("name", "Name or rename a profile");
        command.Options.Add(index);
        command.Options.Add(name);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, token =>
        {
            var newName = parse.GetValue(name);
            new ProfileStore().Rename(parse.GetValue(index), newName ?? "");
            Console.Error.WriteLine($"Profile {parse.GetValue(index)} is now named '{newName!.Trim()}'.");
            return Task.FromResult(ExitCodes.Ok);
        }, ct));
        return command;
    }

    private static Command Who(string commandName, string description)
    {
        var command = new Command(commandName, description);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var format = GlobalOptions.Format(parse);
            var profile = new ProfileStore().Resolve(null, null, Environment.GetEnvironmentVariable, parse.GetValue(GlobalOptions.Profile));
            var result = await new MsalAuth(profile).AcquireSilentAsync(token);

            var row = new JsonObject
            {
                ["profile"] = profile.Name ?? "",
                ["method"] = profile.Method,
                ["tenantId"] = profile.TenantId,
                ["clientId"] = profile.ClientId,
                ["user"] = result.Account.Username,
                ["tokenExpiresOn"] = result.ExpiresOn.UtcDateTime.ToString("o"),
            };
            OutputWriter.Write(Console.Out, [row],
                [new("Profile", "profile"), new("Method", "method"), new("Tenant", "tenantId"), new("Client", "clientId"),
                 new("User", "user"), new("TokenExpiresOn", "tokenExpiresOn")],
                format);
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Token()
    {
        var scope = new Option<string>("--scope")
        {
            Description = "Token for another resource instead, such as https://service.powerapps.com//.default (may open one browser consent prompt)",
        };
        var command = new Command("token", "Print a Power Platform API access token for the active profile (a secret: do not share or log it)");
        command.Options.Add(scope);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var profile = new ProfileStore().Resolve(null, null, Environment.GetEnvironmentVariable, parse.GetValue(GlobalOptions.Profile));
            var auth = new MsalAuth(profile);
            var other = parse.GetValue(scope);
            Console.Out.WriteLine(string.IsNullOrWhiteSpace(other)
                ? await auth.GetTokenAsync(token)
                : await auth.GetTokenForScopeAsync(other.Trim(), Console.Error.WriteLine, token));
            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    private static Command Logout()
    {
        var command = new Command("logout", "Remove the cached sign-in of the active profile and keep the profile");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var store = new ProfileStore();
            var profile = store.Resolve(null, null, Environment.GetEnvironmentVariable, parse.GetValue(GlobalOptions.Profile));

            var sharedWith = ProfileStore.OtherProfileUsingAccount(store.LoadAll(), profile);
            if (sharedWith is null) await new MsalAuth(profile).LogoutAsync();
            if (profile.Name is not null) store.Upsert(profile with { Username = null }, makeActive: false);

            Console.Error.WriteLine(sharedWith is null
                ? "Signed out."
                : $"Signed out of profile '{profile.Name}'. The cached sign-in was kept because profile '{sharedWith.Name}' uses the same account.");
            return ExitCodes.Ok;
        }, ct));
        return command;
    }
}