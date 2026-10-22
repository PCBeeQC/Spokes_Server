namespace Spokes_Server.Core.Services.Reports.Helpers;

using System.Globalization;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.Reports;

/// <summary>
/// Pure formatting and parsing functions for timesheet durations, inputs, and currency amounts.
/// </summary>
public static class TimesheetFormatHelper
{
    private static readonly Regex HourPattern = new(
        @"^(?<sign>[-+])?\s*(?<hours>\d+(?:[.,]\d+)?)\s*h(?:ours?)?(?:\s*(?<mins>\d+)\s*(?:m|min|minutes?)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MinutePattern = new(
        @"^(?<sign>[-+])?\s*(?<mins>\d+)\s*(?:m|min|minutes?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses natural user time inputs into decimal hours.
    /// Supports:
    /// - 'h' notation: 6h, 6h30, 6h 30m, 6.5h, 6,5h
    /// - 'm' notation: 30m, 45 min
    /// - Colon notation: 8:30, 08:30
    /// - Decimal notation: 8.5, 8,5
    /// - Integer quick notation: 8 (8h), 830 (8:30), 85 (85m)
    /// - Negative values: -1h30, -2.5, -8:30
    /// </summary>
    public static decimal ParseTimeInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0;

        input = input.Trim();

        // 1. 'h' notation (e.g. 6h, 6h30, 6h 30m, 6.5h, 6,5h, -1h30)
        var hMatch = HourPattern.Match(input);
        if (hMatch.Success)
        {
            var hoursStr = hMatch.Groups["hours"].Value.Replace(',', '.');
            if (decimal.TryParse(hoursStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var hours))
            {
                var minsStr = hMatch.Groups["mins"].Value;
                if (!string.IsNullOrEmpty(minsStr) && int.TryParse(minsStr, out var mins))
                {
                    hours += mins / 60m;
                }

                if (hMatch.Groups["sign"].Value == "-") hours = -hours;
                return hours;
            }
        }

        // 2. Minute-only notation (e.g. 30m, 45 min, -30m)
        var mMatch = MinutePattern.Match(input);
        if (mMatch.Success && int.TryParse(mMatch.Groups["mins"].Value, out var mOnly))
        {
            var result = mOnly / 60m;
            if (mMatch.Groups["sign"].Value == "-") result = -result;
            return result;
        }

        // 3. Colon format (e.g. 8:30, -8:30)
        var cleanColon = input.EndsWith("h", StringComparison.OrdinalIgnoreCase)
            ? input[..^1].Trim()
            : input;

        if (cleanColon.Contains(':'))
        {
            var parts = cleanColon.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
            {
                var sign = h < 0 || parts[0].TrimStart().StartsWith('-') ? -1m : 1m;
                return (Math.Abs(h) + (m / 60m)) * sign;
            }
        }

        // 4. Decimal format (e.g. 8.5 or 8,5 or -8.5)
        var normalizedInput = input.Contains(',') ? input.Replace(',', '.') : input;
        if (decimal.TryParse(normalizedInput, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) && normalizedInput.Contains('.'))
        {
            return d;
        }

        // 5. Integer Quick Format (e.g. 830 -> 8:30, 8 -> 8:00, 85 -> 85m)
        if (int.TryParse(input, out int val))
        {
            var sign = val < 0 ? -1m : 1m;
            var absVal = Math.Abs(val);

            if (input.TrimStart('-').Length >= 3)
            {
                int hours = absVal / 100;
                int minutes = absVal % 100;
                return sign * (hours + (minutes / 60m));
            }
            if (absVal <= 24)
            {
                return val;
            }
            return sign * (absVal / 60m);
        }

        return 0;
    }

    /// <summary>
    /// Formats decimal hours into a concise "H:mm" string for timesheet grid inputs.
    /// Returns empty string when hours == 0 to allow input placeholders to show.
    /// Safely handles negative durations without producing malformed strings.
    /// </summary>
    public static string FormatHours(decimal hours)
    {
        if (hours == 0) return "";
        var totalMinutes = (int)Math.Round(hours * 60m);
        var sign = totalMinutes < 0 ? "-" : "";
        totalMinutes = Math.Abs(totalMinutes);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        return $"{sign}{h}:{m:D2}";
    }

    public static string FormatDuration(decimal hours, TimeDisplayFormat format)
    {
        if (format == TimeDisplayFormat.DecimalHours)
        {
            return $"{hours:0.00} h";
        }

        var totalMinutes = (int)Math.Round(hours * 60m);
        var sign = totalMinutes < 0 ? "-" : "";
        totalMinutes = Math.Abs(totalMinutes);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        return $"{sign}{h}:{m:D2}";
    }

    public static string FormatCurrency(decimal amount, string symbol = "$")
    {
        return $"{amount:N2} {symbol}".Trim();
    }

    /// <summary>
    /// Formats decimal hours into a concise "XhYY" string (e.g. 24h30, 1h15, 0h30).
    /// </summary>
    public static string FormatDurationHhMm(decimal hours)
    {
        var totalMinutes = (int)Math.Round(hours * 60m);
        var sign = totalMinutes < 0 ? "-" : "";
        totalMinutes = Math.Abs(totalMinutes);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        return $"{sign}{h}h{m:D2}";
    }
}
