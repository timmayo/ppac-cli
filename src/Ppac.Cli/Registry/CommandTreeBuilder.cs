using System.CommandLine;

namespace Ppac.Cli.Registry;

/// <summary>Builds the System.CommandLine tree from the command registry.</summary>
public static class CommandTreeBuilder
{
    public static RootCommand Build(
        IReadOnlyList<CommandSpec> specs,
        IReadOnlyDictionary<string, string> groupDescriptions)
    {
        var root = new RootCommand("ppac - Power Platform admin center CLI");

        foreach (var spec in specs)
        {
            var parent = (Command)root;
            for (var i = 0; i < spec.Path.Length - 1; i++)
            {
                var groupName = spec.Path[i];
                var group = parent.Subcommands.FirstOrDefault(c => c.Name == groupName);
                if (group is null)
                {
                    var key = string.Join(' ', spec.Path.Take(i + 1));
                    groupDescriptions.TryGetValue(key, out var description);
                    group = new Command(groupName, description ?? groupName);
                    parent.Subcommands.Add(group);
                }
                parent = group;
            }

            var command = new Command(spec.Path[^1], spec.Description);
            var options = new Dictionary<string, Option>();

            foreach (var optionSpec in spec.Options)
            {
                var option = CreateOption(optionSpec);
                command.Options.Add(option);
                options[optionSpec.Name] = option;
            }

            var captured = spec;
            command.SetAction(async (parseResult, cancellationToken) =>
                await captured.Handler(new CommandContext(parseResult, options), cancellationToken));

            parent.Subcommands.Add(command);
        }

        return root;
    }

    private static Option CreateOption(OptionSpec spec)
    {
        Option option = spec.Type switch
        {
            OptionType.String => new Option<string>(spec.Name, spec.Aliases),
            OptionType.Int => new Option<int?>(spec.Name, spec.Aliases),
            OptionType.Bool => new Option<bool>(spec.Name, spec.Aliases),
            _ => throw new InvalidOperationException($"Unknown option type {spec.Type}.")
        };

        option.Description = spec.Description;
        option.Required = spec.Required;
        return option;
    }
}