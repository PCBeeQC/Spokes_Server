using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Services.Core;

public class AppLabelService
{
    private readonly Database _db;

    public AppLabelService(Database db)
    {
        _db = db;
    }

    private string Edition => _db.CompanyProfile.Get()?.Edition ?? "Business";

    public string Team => Edition == "Family" ? "Group" : "Team";
    public string Teams => Edition == "Family" ? "Groups" : "Teams";

    public string Employee => Edition == "Family" ? "Member" : "Employee";
    public string Employees => Edition == "Family" ? "Members" : "Employees";

    public string Favorites => Edition == "Family" ? "Menu" : "Favorites";

    public string Company => Edition == "Family" ? "Server" : "Company";
    public string Email => Edition == "Family" ? "Email" : "Business Email";
}
