using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models
{
    public class ExpenseReportTests
    {
        [Fact]
        public void ExpenseReport_Initialization_SetsDefaultsCorrectly()
        {
            var report = new ExpenseReport();

            Assert.False(string.IsNullOrEmpty(report.Id));
            Assert.Equal(string.Empty, report.ReportNumber);
            Assert.Equal(string.Empty, report.EmployeeId);
            Assert.Equal(string.Empty, report.EmployeeName);

            Assert.Equal(DateTime.Today, report.DateCreated);
            Assert.Null(report.DateSubmitted);
            Assert.Null(report.DatePaid);

            Assert.Empty(report.Items);
            Assert.Equal(0, report.TotalAmount);
            Assert.Equal(ExpenseReportStatus.Draft, report.Status);
        }

        [Fact]
        public void ExpenseReport_Status_CalculatedCorrectly()
        {
            var report = new ExpenseReport();
            Assert.Equal(ExpenseReportStatus.Draft, report.Status);

            report.DateSubmitted = DateTime.Today;
            Assert.Equal(ExpenseReportStatus.Submitted, report.Status);

            report.DatePaid = DateTime.Today;
            Assert.Equal(ExpenseReportStatus.Paid, report.Status);
        }

        [Fact]
        public void ExpenseReport_TotalAmount_CalculatedCorrectly()
        {
            var report = new ExpenseReport();
            report.Items.Add(new ExpenseItem { Amount = 100 });
            report.Items.Add(new ExpenseItem { IsKilometrage = true, Kilometers = 50, KilometrageRate = 0.55m });

            // 100 + (50 * 0.55) = 100 + 27.5 = 127.5
            Assert.Equal(127.5m, report.TotalAmount);
        }

        [Fact]
        public void ExpenseItem_Initialization_SetsDefaultsCorrectly()
        {
            var item = new ExpenseItem();

            Assert.False(string.IsNullOrEmpty(item.Id));
            Assert.Equal(string.Empty, item.ProjectId);
            Assert.Equal(DateTime.Today, item.Date);
            Assert.Equal(string.Empty, item.Description);

            Assert.False(item.IsKilometrage);
            Assert.Equal(0, item.Kilometers);
            Assert.Equal(0, item.KilometrageRate);
            Assert.Equal(string.Empty, item.ReceiptPath);
            Assert.Equal(0, item.Amount);
            Assert.Equal(0, item.Total);
        }

        [Fact]
        public void ExpenseItem_Total_CalculatesManualAmount()
        {
            var item = new ExpenseItem { Amount = 150.25m, IsKilometrage = false };
            Assert.Equal(150.25m, item.Total);
        }

        [Fact]
        public void ExpenseItem_Total_CalculatesKilometrage()
        {
            var item = new ExpenseItem
            {
                IsKilometrage = true,
                Kilometers = 100,
                KilometrageRate = 0.60m,
                Amount = 500 // Should ignore Manual Amount if IsKilometrage
            };

            Assert.Equal(60m, item.Total);
        }
    }
}


