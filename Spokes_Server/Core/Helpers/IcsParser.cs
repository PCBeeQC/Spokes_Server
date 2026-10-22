namespace Spokes_Server.Core.Helpers;

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Lightweight model representing basic calendar event data parsed from an ICS stream.
/// </summary>
public class IcsEventData
{
    public string Summary { get; set; } = "Untitled Event";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public bool IsAllDay { get; set; }
    public string? Organizer { get; set; }
}

/// <summary>
/// Lightweight RFC 5545 iCalendar (ICS) parser for extracting event fields without requiring heavy external engines.
/// </summary>
public static class IcsParser
{
    private static readonly string[] DateFormats =
    [
        "yyyyMMdd'T'HHmmss'Z'",
        "yyyyMMdd'T'HHmmss",
        "yyyyMMdd"
    ];

    /// <summary>
    /// Parses the first VEVENT component found in an ICS payload.
    /// </summary>
    public static IcsEventData? ParseFirstEvent(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent)) return null;

        // Unfold lines per RFC 5545 (lines beginning with space or tab continue the previous line)
        var unfolded = Regex.Replace(icsContent, @"\r?\n[ \t]", "");

        using var reader = new StringReader(unfolded);
        string? line;
        bool inEvent = false;
        var eventData = new IcsEventData();
        bool foundEvent = false;

        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                foundEvent = true;
                continue;
            }

            if (trimmed.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!inEvent) continue;

            // Separate property name (and parameters) from value
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            var propHeader = line[..colonIndex];
            var rawValue = line[(colonIndex + 1)..];

            var semicolonIndex = propHeader.IndexOf(';');
            var propName = (semicolonIndex > 0 ? propHeader[..semicolonIndex] : propHeader).Trim().ToUpperInvariant();
            var propParams = semicolonIndex > 0 ? propHeader[(semicolonIndex + 1)..] : "";

            switch (propName)
            {
                case "SUMMARY":
                    eventData.Summary = UnescapeIcsText(rawValue);
                    break;

                case "DESCRIPTION":
                    eventData.Description = UnescapeIcsText(rawValue);
                    break;

                case "LOCATION":
                    eventData.Location = UnescapeIcsText(rawValue);
                    break;

                case "DTSTART":
                    var (dtStart, isAllDayStart) = ParseIcsDateTime(rawValue, propParams);
                    eventData.Start = dtStart;
                    if (isAllDayStart) eventData.IsAllDay = true;
                    break;

                case "DTEND":
                    var (dtEnd, _) = ParseIcsDateTime(rawValue, propParams);
                    eventData.End = dtEnd;
                    break;

                case "ORGANIZER":
                    eventData.Organizer = ParseOrganizer(rawValue, propParams);
                    break;
            }
        }

        return foundEvent ? eventData : null;
    }

    /// <summary>
    /// Converts parsed ICS event data into a domain CalendarEvent.
    /// </summary>
    public static CalendarEvent? ToCalendarEvent(IcsEventData? data, string employeeId)
    {
        if (data == null) return null;

        var start = data.Start ?? DateTime.Today;
        var end = data.End ?? (data.IsAllDay ? start.AddDays(1) : start.AddHours(1));

        if (start.Kind == DateTimeKind.Utc) start = start.ToLocalTime();
        if (end.Kind == DateTimeKind.Utc) end = end.ToLocalTime();

        return new CalendarEvent
        {
            Title = string.IsNullOrWhiteSpace(data.Summary) ? "Untitled Event" : data.Summary,
            Description = data.Description,
            Location = data.Location,
            Start = start,
            End = end,
            IsAllDay = data.IsAllDay,
            EmployeeId = employeeId,
            ExternalParticipant = data.Organizer ?? ""
        };
    }

    private static (DateTime? Value, bool IsAllDay) ParseIcsDateTime(string rawValue, string propParams)
    {
        var val = rawValue.Trim();
        var isAllDay = propParams.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || val.Length == 8;

        if (DateTime.TryParseExact(val, DateFormats, CultureInfo.InvariantCulture,
            val.EndsWith('Z') ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None, out var dt))
        {
            return (dt, isAllDay);
        }

        if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallbackDt))
        {
            return (fallbackDt, isAllDay);
        }

        return (null, isAllDay);
    }

    private static string ParseOrganizer(string rawValue, string propParams)
    {
        // Check CN parameter, e.g. ORGANIZER;CN=Alice:mailto:alice@example.com
        var cnMatch = Regex.Match(propParams, @"CN=(?:""([^""]+)""|([^;]+))", RegexOptions.IgnoreCase);
        if (cnMatch.Success)
        {
            var cn = cnMatch.Groups[1].Success ? cnMatch.Groups[1].Value : cnMatch.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(cn)) return cn.Trim();
        }

        // Fallback to mailto address
        var mailto = rawValue.Trim();
        if (mailto.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return mailto[7..].Trim();
        }

        return mailto;
    }

    private static string UnescapeIcsText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace(@"\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace(@"\N", "\n")
            .Replace(@"\,", ",")
            .Replace(@"\;", ";")
            .Replace(@"\\", "\\");
    }
}
