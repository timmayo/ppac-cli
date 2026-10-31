using Ppac.Cli.Commands;

namespace Ppac.Cli.Registry;

/// <summary>The single list of commands. Add new commands here.</summary>
public static class CommandRegistry
{
    public static readonly IReadOnlyList<CommandSpec> All = AuthCommands.Specs()
        .Concat(EnvironmentCommands.Specs())
        .Concat(BackupCommands.Specs())
        .Concat(EnvironmentGroupCommands.Specs())
        .Concat(EnvironmentGroupWriteCommands.Specs())
        .Concat(EnvironmentManagedCommands.Specs())
        .Concat(OperationCommands.Specs())
        .Concat(CommandsCommand.Specs())
        .ToList();

    /// <summary>Descriptions for command groups (the nouns), keyed by their path.</summary>
    public static readonly IReadOnlyDictionary<string, string> GroupDescriptions =
        new Dictionary<string, string>
        {
            ["auth"] = "Manage authentication profiles.",
            ["environment"] = "Manage Power Platform environments.",
            ["environment backup"] = "Manage environment backups.",
            ["environment group"] = "Manage environment groups.",
            ["environment managed"] = "Turn Managed Environments on or off for an environment.",
            ["environment operation"] = "Check the status of an environment operation.",
            ["environment group operation"] = "Check the status of an environment group operation."
        };
}