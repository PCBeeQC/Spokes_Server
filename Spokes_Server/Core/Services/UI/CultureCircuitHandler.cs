namespace Spokes_Server.Core.Services.UI;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

public class CultureCircuitHandler : CircuitHandler, IDisposable
{
    private readonly CompanyProfileRepository _companyProfiles;

    public CultureCircuitHandler(CompanyProfileRepository companyProfiles)
    {
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _companyProfiles.OnSaved += OnCompanyProfileSaved;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ApplyDateFormat(_companyProfiles.Get()?.DateFormat);
        return Task.CompletedTask;
    }

    private void OnCompanyProfileSaved(CompanyProfile profile)
    {
        ApplyDateFormat(profile.DateFormat);
    }

    public static void ApplyDateFormat(string? format)
    {
        var normalized = DateDisplayFormats.Normalize(format);
        var current = CultureInfo.CurrentCulture;
        var clone = (CultureInfo)current.Clone();
        clone.DateTimeFormat.ShortDatePattern = normalized;
        if (normalized.Contains('/'))
            clone.DateTimeFormat.DateSeparator = "/";
        else if (normalized.Contains('-'))
            clone.DateTimeFormat.DateSeparator = "-";
        else if (normalized.Contains('.'))
            clone.DateTimeFormat.DateSeparator = ".";

        CultureInfo.CurrentCulture = clone;

        var defaultClone = (CultureInfo)(CultureInfo.DefaultThreadCurrentCulture ?? current).Clone();
        defaultClone.DateTimeFormat.ShortDatePattern = normalized;
        defaultClone.DateTimeFormat.DateSeparator = clone.DateTimeFormat.DateSeparator;
        CultureInfo.DefaultThreadCurrentCulture = defaultClone;
    }

    public void Dispose()
    {
        _companyProfiles.OnSaved -= OnCompanyProfileSaved;
    }
}
