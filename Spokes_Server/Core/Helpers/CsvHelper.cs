namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Utility methods for CSV formatting, escaping, and formula injection mitigation.
/// </summary>
public static class CsvHelper
{
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// Escapes a CSV field value per RFC 4180 and sanitizes potential spreadsheet formula injection payloads.
    /// </summary>
    public static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Formula injection mitigation: prefix formula trigger characters with a single quote
        if (FormulaTriggers.Contains(value[0]))
        {
            value = "'" + value;
        }

        const char quote = '"';
        if (value.Contains(',') || value.Contains(quote) || value.Contains('\n') || value.Contains('\r'))
        {
            return quote + value.Replace("\"", "\"\"") + quote;
        }

        return value;
    }
}
