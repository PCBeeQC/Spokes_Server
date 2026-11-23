using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Core.Services.Content;

public class SearchResult
{
    public string Type { get; set; } = string.Empty;   // "Project", "Invoice", etc.
    public string Icon { get; set; } = string.Empty;    // MudBlazor icon string
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class SearchService
{
    private readonly ProjectRepository _projects;
    private readonly EmployeeRepository _employees;
    private readonly QuoteRepository _quotes;
    private readonly InvoiceRepository _invoices;
    private readonly PurchaseOrderRepository _purchases;
    private readonly ExpenseReportRepository _expenseReports;
    private readonly BoardCardRepository _boardCards;
    private readonly BoardRepository _boards;
    private readonly IUserPrivacyService _privacy;
    private readonly UserService _userService;

    public SearchService(
        ProjectRepository projects,
        EmployeeRepository employees,
        QuoteRepository quotes,
        InvoiceRepository invoices,
        PurchaseOrderRepository purchases,
        ExpenseReportRepository expenseReports,
        BoardCardRepository boardCards,
        BoardRepository boards,
        IUserPrivacyService privacy,
        UserService userService)
    {
        _projects = projects;
        _employees = employees;
        _quotes = quotes;
        _invoices = invoices;
        _purchases = purchases;
        _expenseReports = expenseReports;
        _boardCards = boardCards;
        _boards = boards;
        _privacy = privacy;
        _userService = userService;
    }

    public List<SearchResult> Search(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var currentUser = _userService.GetEmployeeAsync(allowSuspended: true).GetAwaiter().GetResult();
        if (currentUser == null || !currentUser.IsActive || currentUser.IsBanned || currentUser.IsSystem)
            return [];

        List<SearchResult> results = [];
        var q = query.Trim();

        bool canAccessAccounting = HasReadPermission(currentUser, AppPermissions.Finance.AccessAccounting);
        bool canAccessExpenses = HasReadPermission(currentUser, AppPermissions.Finance.AccessExpenses);
        bool canViewPo = HasReadPermission(currentUser, AppPermissions.Purchases.ViewPOs)
            || HasReadPermission(currentUser, AppPermissions.Purchases.ManagePOs);
        bool canManagePo = currentUser.IsEffectiveAdmin
            || currentUser.HasEffectivePermission(AppPermissions.Purchases.ManagePOs, allowSuspended: false);

        // 1. Projects
        foreach (var p in _projects.GetAll())
        {
            if (!_userService.CanViewProject(currentUser, p, allowAccountingFinancialRead: false))
                continue;

            if (Matches(p.DisplayName, q) || Matches(p.Client?.BusinessName, q) || Matches(p.ProjectNumber, q) || Matches(p.Description, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Project",
                    Icon = "Icons.Material.Filled.Work",
                    Title = p.DisplayName,
                    Subtitle = string.IsNullOrEmpty(p.Client?.BusinessName) ? p.Status : $"{p.Client.BusinessName} · {p.Status}",
                    Url = $"/projects/{p.Id}"
                });
            }
        }

        // 2. Employees (Filtered through UserPrivacyService and email visibility)
        if (_privacy.CanViewDirectory(currentUser))
        {
            var discoverable = _privacy.FilterDiscoverableEmployees(
                currentUser,
                _employees.GetAll().Where(e => e.IsActive && !e.IsBanned && !e.IsSystem));
            foreach (var e in discoverable)
            {
                bool canManageNonAdminUsers = currentUser.IsActiveUnsuspended &&
                    (currentUser.HasPermission(AppPermissions.Admin.ManageUsers) || currentUser.HasPermission(AppPermissions.Hr.ManageEmployees));
                bool canSeeEmail = e.IsEmailPublic ||
                    currentUser.Id == e.Id ||
                    currentUser.IsEffectiveAdmin ||
                    (!e.IsAdmin && canManageNonAdminUsers);
                if (Matches(e.FullName, q) || (canSeeEmail && Matches(e.Email, q)) || Matches(e.Position, q))
                {
                    string emailPart = canSeeEmail ? e.Email : string.Empty;
                    string subtitle = string.IsNullOrEmpty(e.Position)
                        ? emailPart
                        : (string.IsNullOrEmpty(emailPart) ? e.Position : $"{e.Position} · {emailPart}");

                    results.Add(new SearchResult
                    {
                        Type = "Employee",
                        Icon = "Icons.Material.Filled.Person",
                        Title = e.FullName,
                        Subtitle = subtitle,
                        Url = "/organization"
                    });
                }
            }
        }

        // 3. Quotes
        foreach (var quote in _quotes.GetAll())
        {
            var project = !string.IsNullOrWhiteSpace(quote.ProjectId) ? _projects.GetById(quote.ProjectId) : null;
            if (project == null || !_userService.CanViewProject(currentUser, project, allowAccountingFinancialRead: false))
                continue;

            if (Matches(quote.QuoteNumber, q) || Matches(quote.Title, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Quote",
                    Icon = "Icons.Material.Filled.RequestQuote",
                    Title = $"{quote.QuoteNumber} — {quote.Title}",
                    Subtitle = project.DisplayName,
                    Url = $"/projects/{quote.ProjectId}/quote/{quote.Id}"
                });
            }
        }

        // 4. Invoices
        foreach (var inv in _invoices.GetAll())
        {
            var project = !string.IsNullOrWhiteSpace(inv.ProjectId) ? _projects.GetById(inv.ProjectId) : null;
            bool canViewProject = project != null && _userService.CanViewProject(currentUser, project, allowAccountingFinancialRead: false);
            if (!canAccessAccounting && !canViewProject)
                continue;

            if (Matches(inv.InvoiceNumber, q) || Matches(inv.Note, q))
            {
                string subtitle = project != null && (canAccessAccounting || canViewProject)
                    ? $"{project.DisplayName} · {inv.Status}"
                    : inv.Status;
                string url = !string.IsNullOrWhiteSpace(inv.ProjectGroupId)
                    ? $"/project-groups/{inv.ProjectGroupId}/invoice/{inv.Id}"
                    : (!string.IsNullOrWhiteSpace(inv.ProjectId)
                        ? $"/projects/{inv.ProjectId}/invoice/{inv.Id}"
                        : "/business/accounting");

                results.Add(new SearchResult
                {
                    Type = "Invoice",
                    Icon = "Icons.Material.Filled.Receipt",
                    Title = inv.InvoiceNumber,
                    Subtitle = subtitle,
                    Url = url
                });
            }
        }

        // 5. Purchase Orders
        if (canViewPo)
        {
            foreach (var po in _purchases.GetAll())
            {
                if (po.Status == PurchaseOrderStatus.Void)
                    continue;
                if (po.Status == PurchaseOrderStatus.Draft && !canManagePo)
                    continue;

                if (!currentUser.IsAdmin && po.LineItems != null && po.LineItems.Any(li =>
                    !string.IsNullOrWhiteSpace(li.ProjectId) &&
                    (_projects.GetById(li.ProjectId) is not { } linkedProj ||
                     !_userService.CanViewProject(currentUser, linkedProj, allowAccountingFinancialRead: false))))
                {
                    continue;
                }

                if (Matches(po.PoNumber, q) || Matches(po.Supplier?.BusinessName, q) || Matches(po.IssuedBy, q))
                {
                    results.Add(new SearchResult
                    {
                        Type = "Purchase Order",
                        Icon = "Icons.Material.Filled.ShoppingCart",
                        Title = po.PoNumber,
                        Subtitle = po.Supplier?.BusinessName ?? "",
                        Url = $"/purchases/{po.Id}"
                    });
                }
            }
        }

        // 6. Expense Reports
        foreach (var exp in _expenseReports.GetAll())
        {
            bool canSeeReport = (canAccessExpenses && exp.EmployeeId == currentUser.Id)
                || (canAccessAccounting && exp.Status != ExpenseReportStatus.Draft);
            if (!canSeeReport)
                continue;

            if (Matches(exp.ReportNumber, q) || Matches(exp.EmployeeName, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Expense Report",
                    Icon = "Icons.Material.Filled.AccountBalanceWallet",
                    Title = exp.ReportNumber,
                    Subtitle = $"{exp.EmployeeName} · {exp.Status}",
                    Url = exp.EmployeeId == currentUser.Id ? "/me/expenses" : "/business/accounting"
                });
            }
        }

        // 7. Board Cards
        foreach (var card in _boardCards.GetAll())
        {
            var board = _boards.GetById(card.BoardId);
            if (board == null || !(currentUser.IsAdmin || board.IsPublic || board.OwnerId == currentUser.Id ||
                (board.MemberIds != null && board.MemberIds.Contains(currentUser.Id))))
            {
                continue;
            }

            if (Matches(card.Title, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Board Card",
                    Icon = "Icons.Material.Filled.DashboardCustomize",
                    Title = card.Title,
                    Subtitle = board.Title,
                    Url = $"/boards/{card.BoardId}"
                });
            }
        }

        return results.Take(maxResults).ToList();
    }

    private static bool HasReadPermission(Employee user, string permission) =>
        user.IsAdmin || user.HasEffectivePermission(permission, allowSuspended: true);

    private static bool Matches(string? field, string query)
    {
        if (string.IsNullOrEmpty(field)) return false;
        return field.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
