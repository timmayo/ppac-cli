using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ppac.Core;

namespace Ppac.Cli.Commands;

/// <summary>Escape hatch: call any api.powerplatform.com endpoint with the signed-in admin's token.</summary>
public static class ApiCommand
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Command Create()
    {
        var method = new Argument<string>("method") { Description = "GET, POST, PUT, PATCH or DELETE" };
        var path = new Argument<string>("path")
        {
            Description = "Path including api-version, for example /environmentmanagement/environmentGroups?api-version=2022-03-01-preview",
        };
        var body = new Option<string>("--body") { Description = "JSON request body" };
        var bodyFile = new Option<string>("--body-file") { Description = "Read the JSON request body from a file" };

        var command = new Command("api", "Call any Power Platform API endpoint and print the JSON response");
        command.Arguments.Add(method);
        command.Arguments.Add(path);
        command.Options.Add(body);
        command.Options.Add(bodyFile);

        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, async token =>
        {
            var verb = parse.GetValue(method)!.ToUpperInvariant();
            if (!Methods.Contains(verb))
                throw new PpacException(ExitCodes.Usage, $"Unknown method '{verb}'. Use one of: {string.Join(", ", Methods)}.");

            var inline = parse.GetValue(body);
            var file = parse.GetValue(bodyFile);
            if (inline is not null && file is not null)
                throw new PpacException(ExitCodes.Usage, "Use either --body or --body-file, not both.");
            var json = file is not null ? await File.ReadAllTextAsync(file, token) : inline;

            var response = await CommandRunner.CreateClient(parse)
                .SendAsync(new HttpMethod(verb), parse.GetValue(path)!, json, token);

            if (response.Json is not null)
                Console.Out.WriteLine(response.Json.ToJsonString(Indented));
            else if (!string.IsNullOrWhiteSpace(response.Body))
                Console.Out.WriteLine(response.Body);
            return ExitCodes.Ok;
        }, ct));

        return command;
    }
}
