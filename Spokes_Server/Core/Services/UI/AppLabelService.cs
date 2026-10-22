using Microsoft.Extensions.Localization;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Resources;

namespace Spokes_Server.Core.Services.UI;

public class AppLabelService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly IStringLocalizer<AppResources>? _loc;

    public AppLabelService(CompanyProfileRepository companyProfiles, IStringLocalizer<AppResources>? loc = null)
    {
        _companyProfiles = companyProfiles;
        _loc = loc;
    }

    public AppLabelService(CoreContext coreContext, IStringLocalizer<AppResources>? loc = null)
        : this(coreContext.CompanyProfile, loc)
    {
    }

    private string Edition => _companyProfiles.Get()?.Edition ?? "Business";

    public string Team => Edition == "Family" ? (_loc?["Group"].Value ?? "Group") : (_loc?["Team"].Value ?? "Team");
    public string Teams => Edition == "Family" ? (_loc?["Groups"].Value ?? "Groups") : (_loc?["Teams"].Value ?? "Teams");

    public string Employee => Edition == "Family" ? (_loc?["Member"].Value ?? "Member") : (_loc?["Employee"].Value ?? "Employee");
    public string Employees => Edition == "Family" ? (_loc?["Members"].Value ?? "Members") : (_loc?["Employees"].Value ?? "Employees");

    public string Favorites => Edition == "Family" ? (_loc?["Menu"].Value ?? "Menu") : (_loc?["Favorites"].Value ?? "Favorites");

    public string Company => Edition == "Family" ? (_loc?["Server"].Value ?? "Server") : (_loc?["Company"].Value ?? "Company");
    public string Email => Edition == "Family" ? (_loc?["Email"].Value ?? "Email") : (_loc?["BusinessEmail"].Value ?? "Business Email");
}
