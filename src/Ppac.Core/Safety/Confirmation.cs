namespace Ppac.Core.Safety;

/// <summary>
/// Confirmation for destructive commands. --yes skips it. Without --yes, non-interactive runs fail
/// with exit 2, and interactive runs must type the resource name exactly.
/// </summary>
public sealed class Confirmation
{
    private readonly TextReader _input;
    private readonly TextWriter _prompt;
    private readonly bool _interactive;

    public Confirmation(TextReader input, TextWriter prompt, bool interactive)
    {
        _input = input;
        _prompt = prompt;
        _interactive = interactive;
    }

    public void Require(string action, string resourceName, bool yes)
    {
        if (yes) return;

        if (!_interactive)
            throw new PpacException(ExitCodes.Usage,
                $"Refusing to {action} without --yes in non-interactive mode.");

        _prompt.Write($"About to {action}.{Environment.NewLine}Type '{resourceName}' to confirm: ");
        var typed = _input.ReadLine();
        if (!string.Equals(typed?.Trim(), resourceName, StringComparison.Ordinal))
            throw new PpacException(ExitCodes.General, "Confirmation did not match. Nothing was changed.");
    }
}