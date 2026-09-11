using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
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
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spokes_Server.Tests.Core.Services.Core
{
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
        private readonly SearchService _service;

        public SearchServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Search_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _projects = new ProjectRepository(_persistence, _config);
            _employees = new EmployeeRepository(_persistence, _config);

            var mockSequence = new Mock<SequenceService>(_config);

            _quotes = new QuoteRepository(_persistence, _config, mockSequence.Object, new Mock<ILogger<QuoteRepository>>().Object);
            _invoices = new InvoiceRepository(_persistence, _config, mockSequence.Object);
            _purchases = new PurchaseOrderRepository(_persistence, _config);
            _expenseReports = new ExpenseReportRepository(_persistence, _config, mockSequence.Object);

            _boardCards = new BoardCardRepository(_persistence, _config);
            _boards = new BoardRepository(_persistence, _config);

            _service = new SearchService(
                _projects, _employees, _quotes, _invoices,
                _purchases, _expenseReports, _boardCards, _boards);
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
            _quotes.Save(new Quote { Id = "q1", QuoteNumber = "Q-1234", Title = "Main Quote" });

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
    }
}



