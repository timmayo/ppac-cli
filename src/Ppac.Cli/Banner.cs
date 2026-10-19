using System.Reflection;

namespace Ppac.Cli;

/// <summary>
/// The startup banner. Shown only for a bare "ppac" and for "ppac --help", and only when stderr is a terminal.
/// It goes to stderr so stdout stays clean for scripts. Set PPAC_NO_BANNER to turn it off.
/// </summary>
internal static class Banner
{
    private static readonly string[] Art =
    [
        @"    ____  ____  ___   ______   ________    ____",
        @"   / __ \/ __ \/   | / ____/  / ____/ /   /  _/",
        @"  / /_/ / /_/ / /| |/ /      / /   / /    / /  ",
        @" / ____/ ____/ ___ / /___   / /___/ /____/ /   ",
        @"/_/   /_/   /_/  |_\____/   \____/_____/___/   ",
    ];

    private static readonly string[] HelpFlags = ["-h", "--help", "-?"];

    public static bool ShouldShow(string[] args) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PPAC_NO_BANNER"))
        && !Console.IsErrorRedirected
        && (args.Length == 0 || (args.Length == 1 && HelpFlags.Contains(args[0])));

    /// <summary>The package version without the source-control hash that follows a plus sign.</summary>
    public static string Version()
    {
        var text = typeof(Banner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? typeof(Banner).Assembly.GetName().Version?.ToString()
                   ?? "unknown";
        var plus = text.IndexOf('+');
        return plus < 0 ? text : text[..plus];
    }

    public static void Write(TextWriter writer)
    {
        foreach (var line in Art) writer.WriteLine(line.TrimEnd());
        writer.WriteLine($" Power Platform admin command line tool  |  v{Version()}");
        writer.WriteLine();
    }
}