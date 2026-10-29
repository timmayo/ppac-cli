using System.Reflection;

namespace Ppac.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("ppac - Power Platform admin center CLI");
            Console.WriteLine();
            Console.WriteLine("Usage: ppac <command> [options]");
            Console.WriteLine("       ppac --version");
            return 0;
        }

        if (args[0] is "-v" or "--version")
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            Console.WriteLine(version);
            return 0;
        }

        Console.Error.WriteLine($"Unknown command '{args[0]}'. Run 'ppac --help'.");
        return 1;
    }
}