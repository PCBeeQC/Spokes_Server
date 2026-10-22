namespace Spokes_Server.Core.Constants;

using System;
using System.Collections.Generic;
using System.Linq;

public static class DateDisplayFormats
{
    public const string Iso = "yyyy-MM-dd";
    public const string Uk = "dd/MM/yyyy";
    public const string Us = "MM/dd/yyyy";
    public const string EuroDot = "dd.MM.yyyy";
    public const string AbbrevMonth = "dd MMM yyyy";
    public const string MonthAbbrevDay = "MMM dd, yyyy";

    public static readonly IReadOnlyList<(string Format, string Label)> AvailableFormats =
    [
        (Iso, "yyyy-MM-dd (ISO — 2026-10-14)"),
        (Uk, "dd/MM/yyyy (Global — 14/10/2026)"),
        (Us, "MM/dd/yyyy (US — 10/14/2026)"),
        (EuroDot, "dd.MM.yyyy (Central European — 14.10.2026)"),
        (AbbrevMonth, "dd MMM yyyy (14 Oct 2026)"),
        (MonthAbbrevDay, "MMM dd, yyyy (Oct 14, 2026)")
    ];

    public static string Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return Iso;
        return AvailableFormats.Any(f => f.Format == format) ? format : Iso;
    }
}
