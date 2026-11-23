using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Content;

public class SearchServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly ProjectRepository _projects;
        private readonly EmployeeRepository _employees;
        private readonly QuoteRepository _quotes;
        private readonly InvoiceRepository _invoices;
        private readonly PurchaseOrderRepository _purchases;
        private readonly ExpenseReportRepository _expenseReports;
        private readonly BoardCardRepository _boardCards;
        private readonly BoardRepository _boards;
        private readonly Mock<IUserPrivacyService> _mockPrivacy;
        private readonly Mock<UserService> _mockUserService;
        private readonly SearchService _service;

        public SearchServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Search_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDataDir);

            Dictionary<string, string?> configDict = new() { { "DataPath", _testDataDir }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _projects = new ProjectRepository(_persistence, _config);
            _employees = new EmployeeRepository(_persistence, _config);

            var mockSequence = new Mock<SequenceService>(_config);

            _quotes = new QuoteRepository(_persistence, _config, mockSequence.Object, new Mock<ILogger<QuoteRepository>>().Object);
            _invoices = new InvoiceRepository(_persistence, _config, mockSequence.Object);
            _purchases = new PurchaseOrderRepository(_persistence, _config, mockSequence.Object);
            _expenseReports = new ExpenseReportRepository(_persistence, _config, mockSequence.Object);

            _boardCards = new BoardCardRepository(_persistence, _config);
            _boards = new BoardRepository(_persistence, _config);

            var mockAuthState = new Mock<AuthenticationStateProvider>();
            var openIdAccounts = new OpenIdAccountRepository(_persistence, _config);
            _mockUserService = new Mock<UserService>(mockAuthState.Object, _employees, openIdAccounts, null, null);
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(new Employee
            {
                Id = "test-admin",
                FirstName = "Test",
                LastName = "Admin",
                IsActive = true,
                IsAdmin = true
            });

            _mockPrivacy = new Mock<IUserPrivacyService>();
            _mockPrivacy.Setup(p => p.CanViewDirectory(It.IsAny<Employee?>())).Returns(true);
            _mockPrivacy.Setup(p => p.FilterDiscoverableEmployees(It.IsAny<Employee?>(), It.IsAny<IEnumerable<Employee>>()))
                .Returns<Employee?, IEnumerable<Employee>>((caller, emps) => emps.ToList());

            _service = new SearchService(
                _projects, _employees, _quotes, _invoices,
                _purchases, _expenseReports, _boardCards, _boards,
                _mockPrivacy.Object, _mockUserService.Object);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public void Search_ReturnsEmpty_ForShortOrEmptyQuery()
        {
            Assert.Empty(_service.Search(""));
            Assert.Empty(_service.Search("a"));
            Assert.Empty(_service.Search("  "));
        }

        [Fact]
        public void Search_FindsProjects_ByDisplayNameOrNumber()
        {
            _projects.Save(new Project { Id = "p1", Name = "Alpha Project", ProjectNumber = "P-100" });
            _projects.Save(new Project { Id = "p2", Name = "Beta Project", ProjectNumber = "P-200" });

            var results = _service.Search("Alpha");
            Assert.Single(results);
            Assert.Equal("Project", results[0].Type);
            Assert.Equal("P-100 - Alpha Project", results[0].Title);

            results = _service.Search("P-200");
            Assert.Single(results);
            Assert.Contains("Beta Project", results[0].Title);
        }

        [Fact]
        public void Search_FindsEmployees_ByFullNameOrEmail()
        {
            _employees.Save(new Employee { Id = "e1", FirstName = "John", LastName = "Doe", Email = "john@spokes.app" });

            var results = _service.Search("john");
            Assert.Single(results);
            Assert.Equal("Employee", results[0].Type);
            Assert.Equal("John Doe", results[0].Title);
        }

        [Fact]
        public void Search_FindsQuotes_ByNumberOrTitle()
        {
            _projects.Save(new Project { Id = "p-q1", Name = "Quote Project", AccessPolicy = ProjectAccessPolicy.Public });
            _quotes.Save(new Quote { Id = "q1", ProjectId = "p-q1", QuoteNumber = "Q-1234", Title = "Main Quote" });

            var results = _service.Search("Q-1234");
            Assert.Single(results);
            Assert.Equal("Quote", results[0].Type);
            Assert.Contains("Main Quote", results[0].Title);
        }

        [Fact]
        public void Search_LimitsResults()
        {
            for (int i = 0; i < 30; i++)
            {
                _projects.Save(new Project { Id = $"p{i}", Name = $"Project {i}" });
            }

            var results = _service.Search("Project", maxResults: 10);
            Assert.Equal(10, results.Count);
        }

        [Fact]
        public void Search_IsCaseInsensitive()
        {
            _projects.Save(new Project { Id = "p1", Name = "CASE TEST" });

            var results = _service.Search("case");
            Assert.Single(results);
            Assert.Contains("CASE TEST", results[0].Title);
        }

        [Fact]
        public void Search_FindsInvoices_ByInvoiceNumberOrNote()
        {
            var project = new Project
            {
                Id = "p-inv",
                Name = "Consulting Project",
                ProjectNumber = "P-INV"
            };
            _projects.Save(project);

            var invoice = new Invoice
            {
                Id = "inv-1",
                InvoiceNumber = "INV-2024-001",
                Note = "Special consultation note",
                ProjectId = "p-inv",
                Status = "Sent"
            };
            _invoices.Save(invoice);

            var byNumber = _service.Search("INV-2024");
            Assert.Single(byNumber);
            Assert.Equal("Invoice", byNumber[0].Type);
            Assert.Equal("INV-2024-001", byNumber[0].Title);
            Assert.Contains(project.DisplayName, byNumber[0].Subtitle);
            Assert.Contains("Sent", byNumber[0].Subtitle);
            Assert.Equal("/projects/p-inv/invoice/inv-1", byNumber[0].Url);

            var byNote = _service.Search("consultation");
            Assert.Single(byNote);
            Assert.Equal("Invoice", byNote[0].Type);
            Assert.Equal("INV-2024-001", byNote[0].Title);
            Assert.Contains(project.DisplayName, byNote[0].Subtitle);
            Assert.Contains("Sent", byNote[0].Subtitle);
            Assert.Equal("/projects/p-inv/invoice/inv-1", byNote[0].Url);
        }

        [Fact]
        public void Search_FindsPurchaseOrders_ByPoNumberSupplierOrIssuedBy()
        {
            var po = new PurchaseOrder
            {
                Id = "po-1",
                PoNumber = "PO-999",
                Supplier = new ClientInfo { BusinessName = "Acme Supplies" },
                IssuedBy = "FinanceManager"
            };
            _purchases.Save(po);

            var byPoNumber = _service.Search("PO-999");
            Assert.Single(byPoNumber);
            Assert.Equal("Purchase Order", byPoNumber[0].Type);
            Assert.Equal("PO-999", byPoNumber[0].Title);
            Assert.Equal($"/purchases/{po.Id}", byPoNumber[0].Url);

            var bySupplier = _service.Search("Acme");
            Assert.Single(bySupplier);
            Assert.Equal("Purchase Order", bySupplier[0].Type);
            Assert.Equal($"/purchases/{po.Id}", bySupplier[0].Url);

            var byIssuedBy = _service.Search("FinanceManager");
            Assert.Single(byIssuedBy);
            Assert.Equal("Purchase Order", byIssuedBy[0].Type);
            Assert.Equal($"/purchases/{po.Id}", byIssuedBy[0].Url);
        }

        [Fact]
        public void Search_FindsExpenseReports_ByReportNumberOrEmployeeName()
        {
            var report = new ExpenseReport
            {
                Id = "exp-1",
                ReportNumber = "EXP-777",
                EmployeeId = "test-admin",
                EmployeeName = "Jane Smith",
                DateSubmitted = DateTime.Today
            };
            _expenseReports.Save(report);

            var byNumber = _service.Search("EXP-777");
            Assert.Single(byNumber);
            Assert.Equal("Expense Report", byNumber[0].Type);
            Assert.Equal("Jane Smith · Submitted", byNumber[0].Subtitle);
            Assert.Equal("/me/expenses", byNumber[0].Url);

            var otherReport = new ExpenseReport
            {
                Id = "exp-other-1",
                ReportNumber = "EXP-888",
                EmployeeId = "emp-other",
                EmployeeName = "Mark Other",
                DateSubmitted = DateTime.Today
            };
            _expenseReports.Save(otherReport);

            var otherResults = _service.Search("EXP-888");
            Assert.Single(otherResults);
            Assert.Equal("/business/accounting", otherResults[0].Url);

            var byEmployee = _service.Search("Jane");
            Assert.Single(byEmployee);
            Assert.Equal("Expense Report", byEmployee[0].Type);
            Assert.Equal("Jane Smith · Submitted", byEmployee[0].Subtitle);
            Assert.Equal("/me/expenses", byEmployee[0].Url);
        }

        [Fact]
        public void Search_FindsBoardCards_ByTitle()
        {
            var board = new Board
            {
                Id = "b1",
                Title = "Dev Board"
            };
            _boards.Save(board);

            var card = new BoardCard
            {
                Id = "card-1",
                BoardId = "b1",
                Title = "Implement OAuth"
            };
            _boardCards.Save(card);

            var results = _service.Search("OAuth");
            Assert.Single(results);
            Assert.Equal("Board Card", results[0].Type);
            Assert.Equal("Implement OAuth", results[0].Title);
            Assert.Equal("Dev Board", results[0].Subtitle);
            Assert.Equal("/boards/b1", results[0].Url);
        }

        [Fact]
        public void Search_ExcludesInactiveEmployees()
        {
            var inactiveEmployee = new Employee
            {
                Id = "e-inactive",
                FirstName = "Ghost",
                LastName = "Employee",
                Email = "ghost@spokes.app",
                IsActive = false
            };
            _employees.Save(inactiveEmployee);

            var results = _service.Search("Ghost");
            Assert.Empty(results);
        }

        [Fact]
        public void Search_ProjectSubtitleFormatting()
        {
            var projectWithClient = new Project
            {
                Id = "p-client",
                Name = "Project Alpha",
                Status = "Active",
                Client = new ClientInfo { BusinessName = "Acme Corp" }
            };
            var projectWithoutClient = new Project
            {
                Id = "p-no-client",
                Name = "Internal Beta",
                Status = "Draft",
                Client = new ClientInfo { BusinessName = "" }
            };
            _projects.Save(projectWithClient);
            _projects.Save(projectWithoutClient);

            var resultsWithClient = _service.Search("Project Alpha");
            Assert.Single(resultsWithClient);
            Assert.Equal("Acme Corp · Active", resultsWithClient[0].Subtitle);

            var resultsWithoutClient = _service.Search("Internal Beta");
            Assert.Single(resultsWithoutClient);
            Assert.Equal("Draft", resultsWithoutClient[0].Subtitle);
        }

        [Fact]
        public void Search_EmployeeSubtitleFormatting()
        {
            var employeeWithPosition = new Employee
            {
                Id = "e-pos",
                FirstName = "Alice",
                LastName = "Smith",
                Email = "alice@spokes.app",
                Position = "Software Engineer",
                IsActive = true
            };
            var employeeWithoutPosition = new Employee
            {
                Id = "e-nopos",
                FirstName = "Bob",
                LastName = "Jones",
                Email = "bob@spokes.app",
                Position = "",
                IsActive = true
            };
            _employees.Save(employeeWithPosition);
            _employees.Save(employeeWithoutPosition);

            var resultsWithPosition = _service.Search("Alice");
            Assert.Single(resultsWithPosition);
            Assert.Equal("Software Engineer · alice@spokes.app", resultsWithPosition[0].Subtitle);

            var resultsWithoutPosition = _service.Search("Bob");
            Assert.Single(resultsWithoutPosition);
            Assert.Equal("bob@spokes.app", resultsWithoutPosition[0].Subtitle);
        }

        [Fact]
        public void Search_RespectsPrivacy_WhenCanViewDirectoryIsFalse()
        {
            _employees.Save(new Employee { Id = "e-hidden", FirstName = "Secret", LastName = "User", Email = "secret@spokes.app", IsActive = true });
            _mockPrivacy.Setup(p => p.CanViewDirectory(It.IsAny<Employee?>())).Returns(false);

            var results = _service.Search("Secret");
            Assert.Empty(results);
        }

        [Fact]
        public void Search_RespectsPrivacy_FiltersUndiscoverableEmployees()
        {
            var emp1 = new Employee { Id = "e1", FirstName = "Allowed", LastName = "User", Email = "allowed@spokes.app", IsActive = true };
            var emp2 = new Employee { Id = "e2", FirstName = "Forbidden", LastName = "User", Email = "forbidden@spokes.app", IsActive = true };
            _employees.Save(emp1);
            _employees.Save(emp2);

            _mockPrivacy.Setup(p => p.CanViewDirectory(It.IsAny<Employee?>())).Returns(true);
            _mockPrivacy.Setup(p => p.FilterDiscoverableEmployees(It.IsAny<Employee?>(), It.IsAny<IEnumerable<Employee>>()))
                .Returns<Employee?, IEnumerable<Employee>>((caller, emps) => emps.Where(e => e.Id == "e1").ToList());

            var results = _service.Search("User");
            Assert.Single(results);
            Assert.Equal("Allowed User", results[0].Title);
        }

        [Fact]
        public void Search_ReturnsEmpty_WhenCallerNullInactiveBannedOrSystem()
        {
            _projects.Save(new Project { Id = "p-open-auth", Name = "Open Beacon", AccessPolicy = ProjectAccessPolicy.Public });
            _employees.Save(new Employee { Id = "e-open-auth", FirstName = "Beacon", LastName = "User", IsActive = true });

            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync((Employee?)null);
            Assert.Empty(_service.Search("Beacon"));

            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(new Employee { Id = "u-inact", IsActive = false, IsAdmin = true });
            Assert.Empty(_service.Search("Beacon"));

            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(new Employee { Id = "u-ban", IsActive = true, IsBanned = true, IsAdmin = true });
            Assert.Empty(_service.Search("Beacon"));

            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(new Employee { Id = "u-sys", IsActive = true, IsSystem = true, IsAdmin = true });
            Assert.Empty(_service.Search("Beacon"));
        }

        [Fact]
        public void Search_EnforcesRestrictedProjectAclAcrossProjectsQuotesInvoicesAndPurchaseOrders_AndGroupInvoiceUrl()
        {
            _projects.Save(new Project
            {
                Id = "p-sec",
                Name = "Apollo Classified",
                Description = "OmegaSecretPhrase",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedUserIds = ["emp-member"]
            });
            _quotes.Save(new Quote { Id = "q-sec", ProjectId = "p-sec", QuoteNumber = "Q-SEC-1", Title = "Classified Quote" });
            _invoices.Save(new Invoice { Id = "inv-sec", ProjectId = "p-sec", ProjectGroupId = "pg-9", InvoiceNumber = "INV-SEC-1", Status = "Sent" });
            _purchases.Save(new PurchaseOrder
            {
                Id = "po-sec",
                PoNumber = "PO-SEC-1",
                Status = PurchaseOrderStatus.Final,
                Supplier = new ClientInfo { BusinessName = "Restricted Vendor" },
                LineItems = [new PoItem { Description = "Part", Quantity = 1m, UnitPrice = 10m, ProjectId = "p-sec" }]
            });

            var outsider = new Employee
            {
                Id = "emp-outsider",
                IsActive = true,
                Permissions = [AppPermissions.Projects.View, AppPermissions.Purchases.ViewPOs]
            };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(outsider);

            Assert.Empty(_service.Search("Apollo"));
            Assert.Empty(_service.Search("OmegaSecretPhrase"));
            Assert.Empty(_service.Search("Q-SEC-1"));
            Assert.Empty(_service.Search("INV-SEC-1"));
            Assert.Empty(_service.Search("PO-SEC-1"));

            var member = new Employee
            {
                Id = "emp-member",
                IsActive = true,
                Permissions = [AppPermissions.Projects.View, AppPermissions.Purchases.ViewPOs]
            };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(member);

            Assert.Single(_service.Search("Apollo"));
            Assert.Single(_service.Search("Q-SEC-1"));
            var invHit = Assert.Single(_service.Search("INV-SEC-1"));
            Assert.Equal("/project-groups/pg-9/invoice/inv-sec", invHit.Url);
            Assert.Single(_service.Search("PO-SEC-1"));
        }

        [Fact]
        public void Search_HidesPrivateEmployeeEmailFromNonAdminPeer_InQueryAndSubtitle()
        {
            _employees.Save(new Employee
            {
                Id = "e-priv",
                FirstName = "Clara",
                LastName = "Vance",
                Email = "clara.secret@spokes.app",
                Position = "Architect",
                IsEmailPublic = false,
                IsActive = true
            });

            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(new Employee
            {
                Id = "e-peer",
                IsActive = true,
                IsAdmin = false,
                Permissions = []
            });

            Assert.Empty(_service.Search("clara.secret"));
            var nameHit = Assert.Single(_service.Search("Clara"));
            Assert.Equal("Clara Vance", nameHit.Title);
            Assert.Equal("Architect", nameHit.Subtitle);
        }

        [Fact]
        public void Search_EnforcesDraftPo_DraftExpenseReport_AndPrivateBoardCardRules()
        {
            _purchases.Save(new PurchaseOrder { Id = "po-drf-10", PoNumber = "PO-DRF-10", Status = PurchaseOrderStatus.Draft });
            _purchases.Save(new PurchaseOrder { Id = "po-void-10", PoNumber = "PO-VOID-10", Status = PurchaseOrderStatus.Void });

            var viewPoOnly = new Employee { Id = "emp-po-view", IsActive = true, Permissions = [AppPermissions.Purchases.ViewPOs] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(viewPoOnly);
            Assert.Empty(_service.Search("PO-DRF-10"));
            Assert.Empty(_service.Search("PO-VOID-10"));

            var managePo = new Employee { Id = "emp-po-mgr", IsActive = true, Permissions = [AppPermissions.Purchases.ManagePOs] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(managePo);
            Assert.Single(_service.Search("PO-DRF-10"));
            Assert.Empty(_service.Search("PO-VOID-10"));

            _expenseReports.Save(new ExpenseReport
            {
                Id = "exp-drf-99",
                ReportNumber = "EXP-DRF-99",
                EmployeeId = "emp-owner",
                EmployeeName = "Expense Owner",
                DateSubmitted = null
            });

            var accountantNonOwner = new Employee { Id = "emp-fin", IsActive = true, Permissions = [AppPermissions.Finance.AccessAccounting] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(accountantNonOwner);
            Assert.Empty(_service.Search("EXP-DRF-99"));

            var owner = new Employee { Id = "emp-owner", IsActive = true, Permissions = [AppPermissions.Finance.AccessExpenses] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(owner);
            var ownHit = Assert.Single(_service.Search("EXP-DRF-99"));
            Assert.Equal("/me/expenses", ownHit.Url);

            _boards.Save(new Board
            {
                Id = "b-priv",
                Title = "Exec Board",
                IsPublic = false,
                OwnerId = "emp-owner",
                MemberIds = ["emp-board-mem"]
            });
            _boardCards.Save(new BoardCard
            {
                Id = "c-priv",
                BoardId = "b-priv",
                Title = "Secret Acquisition"
            });

            var nonMember = new Employee { Id = "emp-nonboard", IsActive = true, Permissions = [] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(nonMember);
            Assert.Empty(_service.Search("Acquisition"));

            var boardMember = new Employee { Id = "emp-board-mem", IsActive = true, Permissions = [] };
            _mockUserService.Setup(u => u.GetEmployeeAsync()).ReturnsAsync(boardMember);
            Assert.Single(_service.Search("Acquisition"));
        }
    }

