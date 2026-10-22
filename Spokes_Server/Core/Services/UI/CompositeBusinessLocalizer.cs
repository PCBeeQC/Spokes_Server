using Microsoft.Extensions.Localization;
using Spokes_Server.Resources;

namespace Spokes_Server.Core.Services.UI;

/// <summary>
/// Composite localizer for Business Edition strings.
/// Queries BusinessResources first; if the requested key is not found,
/// gracefully falls back to AppResources (so shared terms like Save, Cancel, Delete
/// do not need to be duplicated in BusinessResources).
/// </summary>
public class CompositeBusinessLocalizer : IStringLocalizer<BusinessResources>
{
    private readonly IStringLocalizer _businessLocalizer;
    private readonly IStringLocalizer<AppResources> _appLocalizer;

    public CompositeBusinessLocalizer(IStringLocalizerFactory factory, IStringLocalizer<AppResources> appLocalizer)
    {
        _businessLocalizer = factory.Create(typeof(BusinessResources));
        _appLocalizer = appLocalizer;
    }

    public LocalizedString this[string name]
    {
        get
        {
            var res = _businessLocalizer[name];
            if (!res.ResourceNotFound)
            {
                return res;
            }

            var appRes = _appLocalizer[name];
            if (!appRes.ResourceNotFound)
            {
                return appRes;
            }

            return res;
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var res = _businessLocalizer[name, arguments];
            if (!res.ResourceNotFound)
            {
                return res;
            }

            var appRes = _appLocalizer[name, arguments];
            if (!appRes.ResourceNotFound)
            {
                return appRes;
            }

            return res;
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var businessStrings = _businessLocalizer.GetAllStrings(includeParentCultures);
        var appStrings = _appLocalizer.GetAllStrings(includeParentCultures);
        return businessStrings.Concat(appStrings);
    }
}
