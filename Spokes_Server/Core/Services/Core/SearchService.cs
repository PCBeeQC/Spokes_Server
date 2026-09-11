using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Core;

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

    public SearchService(
        ProjectRepository projects,
        EmployeeRepository employees,
        QuoteRepository quotes,
        InvoiceRepository invoices,
        PurchaseOrderRepository purchases,
        ExpenseReportRepository expenseReports,
        BoardCardRepository boardCards,
        BoardRepository boards)
    {
        _projects = projects;
        _employees = employees;
        _quotes = quotes;
        _invoices = invoices;
        _purchases = purchases;
        _expenseReports = expenseReports;
        _boardCards = boardCards;
        _boards = boards;
    }

    public List<SearchResult> Search(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return new List<SearchResult>();

        var results = new List<SearchResult>();
        var q = query.Trim();

        // 1. Projects
        foreach (var p in _projects.GetAll())
        {
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

        // 2. Employees
        foreach (var e in _employees.GetAll().Where(e => e.IsActive))
        {
            if (Matches(e.FullName, q) || Matches(e.Email, q) || Matches(e.Position, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Employee",
                    Icon = "Icons.Material.Filled.Person",
                    Title = e.FullName,
                    Subtitle = string.IsNullOrEmpty(e.Position) ? e.Email : $"{e.Position} · {e.Email}",
                    Url = "/admin/organization"
                });
            }
        }

        // 3. Quotes
        foreach (var quote in _quotes.GetAll())
        {
            if (Matches(quote.QuoteNumber, q) || Matches(quote.Title, q))
            {
                var project = _projects.GetById(quote.ProjectId);
                results.Add(new SearchResult
                {
                    Type = "Quote",
                    Icon = "Icons.Material.Filled.RequestQuote",
                    Title = $"{quote.QuoteNumber} — {quote.Title}",
                    Subtitle = project?.DisplayName ?? "",
                    Url = $"/projects/{quote.ProjectId}/quote/{quote.Id}"
                });
            }
        }

        // 4. Invoices
        foreach (var inv in _invoices.GetAll())
        {
            if (Matches(inv.InvoiceNumber, q) || Matches(inv.Note, q))
            {
                var project = _projects.GetById(inv.ProjectId);
                results.Add(new SearchResult
                {
                    Type = "Invoice",
                    Icon = "Icons.Material.Filled.Receipt",
                    Title = inv.InvoiceNumber,
                    Subtitle = $"{project?.DisplayName ?? ""} · {inv.Status}",
                    Url = $"/projects/{inv.ProjectId}/invoice/{inv.Id}"
                });
            }
        }

        // 5. Purchase Orders
        foreach (var po in _purchases.GetAll())
        {
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

        // 6. Expense Reports
        foreach (var exp in _expenseReports.GetAll())
        {
            if (Matches(exp.ReportNumber, q) || Matches(exp.EmployeeName, q))
            {
                results.Add(new SearchResult
                {
                    Type = "Expense Report",
                    Icon = "Icons.Material.Filled.AccountBalanceWallet",
                    Title = exp.ReportNumber,
                    Subtitle = $"{exp.EmployeeName} · {exp.Status}",
                    Url = "/me/expenses"
                });
            }
        }

        // 7. Board Cards
        foreach (var card in _boardCards.GetAll())
        {
            if (Matches(card.Title, q))
            {
                var board = _boards.GetById(card.BoardId);
                results.Add(new SearchResult
                {
                    Type = "Board Card",
                    Icon = "Icons.Material.Filled.DashboardCustomize",
                    Title = card.Title,
                    Subtitle = board?.Title ?? "",
                    Url = $"/boards/{card.BoardId}"
                });
            }
        }

        return results.Take(maxResults).ToList();
    }

    private static bool Matches(string? field, string query)
    {
        if (string.IsNullOrEmpty(field)) return false;
        return field.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}



