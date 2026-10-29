using System.CommandLine;

namespace Ppac.Cli.Registry;

/// <summary>What a command does to the tenant or the machine.</summary>
public enum CommandClass
{
    Read,
    Write,
    Destructive
}

/// <summary>
/// How well the command's API is confirmed. Only Verified, non-interactive
/// read commands are eligible to be exposed as MCP tools.
/// </summary>
public enum VerificationStatus
{
    Verified,
    OperationOnly,
    Unverified
}

public enum OptionType
{
    String,
    Int,
    Bool
}

public sealed record OptionSpec(
    string Name,
    string[] Aliases,
    OptionType Type,
    string Description,
    bool Required = false);

/// <summary>
/// One command. The CLI parser, 'ppac commands --json' and (later) the MCP server
/// are all generated from these, so they cannot drift apart.
/// </summary>
public sealed class CommandSpec
{
    public required string[] Path { get; init; }
    public required string Description { get; init; }
    public required CommandClass Class { get; init; }
    public required VerificationStatus Status { get; init; }
    public required Func<CommandContext, CancellationToken, Task<int>> Handler { get; init; }

    /// <summary>The API operation or local action behind the command.</summary>
    public string? Api { get; init; }

    /// <summary>The Power Platform API permission the command needs, when known.</summary>
    public string? Permission { get; init; }

    public bool Preview { get; init; }

    /// <summary>True when the command needs a person (for example a browser sign-in).</summary>
    public bool Interactive { get; init; }

    public IReadOnlyList<OptionSpec> Options { get; init; } = Array.Empty<OptionSpec>();

    public string Name => string.Join(' ', Path);

    public bool McpEligible =>
        Status == VerificationStatus.Verified && Class == CommandClass.Read && !Interactive;
}

/// <summary>Typed access to the parsed option values for one command run.</summary>
public sealed class CommandContext
{
    private readonly ParseResult _parseResult;
    private readonly IReadOnlyDictionary<string, Option> _options;

    public CommandContext(ParseResult parseResult, IReadOnlyDictionary<string, Option> options)
    {
        _parseResult = parseResult;
        _options = options;
    }

    public string? GetString(string name) => _parseResult.GetValue((Option<string>)_options[name]);

    public int? GetInt(string name) => _parseResult.GetValue((Option<int?>)_options[name]);

    public bool GetBool(string name) => _parseResult.GetValue((Option<bool>)_options[name]);
}