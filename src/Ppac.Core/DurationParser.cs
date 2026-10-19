using System.Globalization;

namespace Ppac.Core;

public static class DurationParser
{
    /// <summary>Parses "30s", "10m", "2h" or a bare number of seconds. Anything else is a usage error.</summary>
    public static TimeSpan Parse(string text, string optionName)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        var unit = trimmed.Length > 0 ? trimmed[^1] : ' ';
        var numberText = char.IsLetter(unit) ? trimmed[..^1] : trimmed;

        var factor = unit switch
        {
            's' => 1,
            'm' => 60,
            'h' => 3600,
            _ when char.IsDigit(unit) => 1,
            _ => 0,
        };

        if (factor == 0 ||
            !double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < 0)
            throw new PpacException(ExitCodes.Usage,
                $"Invalid {optionName} '{text}'. Use a number with s, m or h, for example 30s, 10m or 2h.");

        return TimeSpan.FromSeconds(number * factor);
    }
}