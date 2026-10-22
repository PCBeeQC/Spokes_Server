namespace Spokes_Server.Core.Extensions;

using System;

public static class DateDisplayExtensions
{
    public static string ToCompanyDate(this DateTime date) => date.ToShortDateString();

    public static string ToCompanyDate(this DateTime? date, string fallback = "-") =>
        date.HasValue ? date.Value.ToShortDateString() : fallback;
}
