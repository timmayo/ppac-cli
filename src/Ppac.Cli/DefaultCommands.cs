namespace Ppac.Cli;

/// <summary>
/// A command group typed without a subcommand runs a sensible default instead of failing with
/// "Required command was not provided": "ppac env" runs "ppac env list". Groups whose list needs an
/// argument (op, env backup) show their help instead. Other options keep working: "ppac env -o json"
/// becomes "ppac env list -o json".
/// </summary>
internal static class DefaultCommands
{
    private const string Help = "--help";

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["env"] = "list",
        ["group"] = "list",
        ["inventory"] = "list",
        ["auth"] = "list",
        ["op"] = Help,
        ["env backup"] = Help,
    };

    // Global options that take a value, so the value is not mistaken for a command.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-o", "--output", "--profile",
    };

    private static readonly string[] HelpFlags = ["-h", "--help", "-?", "--version"];

    public static string[] Apply(string[] args, TextWriter hint)
    {
        if (args.Any(a => HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase))) return args;

        var path = new List<string>();
        var last = -1;
        for (var i = 0; i < args.Length; i++)
        {
            if (ValueOptions.Contains(args[i]))
            {
                i++;
                continue;
            }

            if (args[i].StartsWith('-')) continue;
            path.Add(args[i]);
            last = i;
        }

        var key = string.Join(' ', path);
        if (path.Count == 0 || !Defaults.TryGetValue(key, out var action)) return args;

        if (action != Help && !Console.IsErrorRedirected)
            hint.WriteLine($"No command given, running 'ppac {key} {action}'. Run 'ppac {key} --help' for all commands.");

        return [.. args.Take(last + 1), action, .. args.Skip(last + 1)];
    }
}