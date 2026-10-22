using System;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Maintenance.Time;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Helper for resolving company-configured timezones and converting UTC timestamps.
/// </summary>
public static class TimeZoneHelper
{
    /// <summary>
    /// Converts a UTC time (or authoritative clock UTC if omitted) to the timezone configured in the company profile.
    /// Falls back safely to server local time if not configured, invalid, or not found on the host OS.
    /// </summary>
    public static DateTime GetCompanyTime(CompanyProfileRepository? companyProfileRepo, DateTime? utc = null)
    {
        var tzId = companyProfileRepo?.Get()?.TimeZoneId;
        return ConvertUtcToCompanyTime(tzId, utc);
    }

    /// <summary>
    /// Converts a UTC time (or authoritative clock UTC if omitted) to the given timezone ID.
    /// Falls back safely to server local time if not found or invalid.
    /// </summary>
    public static DateTime ConvertUtcToCompanyTime(string? timeZoneId, DateTime? utc = null)
    {
        var targetUtc = utc ?? SpokesClockService.Instance.UtcNow;

        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(targetUtc, tz);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback to local server time if timezone ID is not found on the OS
            }
            catch (InvalidTimeZoneException)
            {
                // Fallback to local server time if timezone data is invalid
            }
        }

        if (utc == null)
        {
            return SpokesClockService.Instance.Now;
        }

        return utc.Value.Kind == DateTimeKind.Utc ? utc.Value.ToLocalTime() : utc.Value;
    }
}
