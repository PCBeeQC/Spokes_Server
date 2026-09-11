using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Tests.Core.Models
{
    public class ProjectsTests
    {
        [Fact]
        public void Project_Initialization_SetsDefaultsCorrectly()
        {
            var project = new Project();

            Assert.False(string.IsNullOrEmpty(project.Id));
            Assert.Equal(string.Empty, project.ProjectNumber);
            Assert.Equal(string.Empty, project.Name);
            Assert.Equal(string.Empty, project.DisplayName);
            Assert.NotNull(project.Client);
            Assert.Equal(string.Empty, project.Description);
            Assert.Equal(ProjectStatus.Draft, project.Status);
            Assert.Null(project.RateCardId);
            Assert.Empty(project.CustomRates);
            Assert.False(project.IsInternal);
            Assert.True(project.IsActive);
            Assert.True(project.CreatedAt <= DateTime.UtcNow);
            Assert.Equal(BillingMethods.TimeAndMaterials, project.BillingMethod);
            Assert.Null(project.DefaultTemplateId);
            Assert.Null(project.ProjectManagerId);
            Assert.Empty(project.Commissions);
            Assert.Equal("Public", project.AccessPolicy);
            Assert.Empty(project.AllowedTeamIds);
            Assert.Empty(project.AllowedUserIds);
            Assert.Empty(project.ApprovedWorkTypeIds);
            Assert.Empty(project.Allocations);
            Assert.Empty(project.PurchaseOrders);
            Assert.NotEmpty(project.NoteCategories);
            Assert.Empty(project.NoteCategoryColors);
            Assert.Null(project.ProjectGroupId);
        }

        [Fact]
        public void Project_DisplayName_ConcatenatesProperly()
        {
            var p1 = new Project { Name = "Test Project" };
            Assert.Equal("Test Project", p1.DisplayName);

            var p2 = new Project { ProjectNumber = "P100", Name = "Test Project" };
            Assert.Equal("P100 - Test Project", p2.DisplayName);
        }

        [Fact]
        public void GetEffectiveRate_PrioritizesCorrectly()
        {
            var project = new Project();
            project.CustomRates["WorkA"] = 150m;

            var rateCard = new RateCard();
            rateCard.Rates["WorkA"] = 120m;
            rateCard.Rates["WorkB"] = 100m;

            // 1. Project Override
            Assert.Equal(150m, project.GetEffectiveRate("WorkA", 80m, rateCard));

            // 2. Rate Card
            Assert.Equal(100m, project.GetEffectiveRate("WorkB", 80m, rateCard));

            // 3. System Default
            Assert.Equal(80m, project.GetEffectiveRate("WorkC", 80m, rateCard));
        }

        [Fact]
        public void GetAvailableCredit_CalculatesCorrectly()
        {
            var project = new Project();
            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Status = "Paid",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = true, Quantity = 1, UnitPrice = 1000 }
                    }
                },
                new Invoice
                {
                    Status = "Sent", // Not paid yet, should not count towards earned
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = true, Quantity = 1, UnitPrice = 500 }
                    }
                },
                new Invoice
                {
                    Status = "Draft",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsCreditUsed = true, Quantity = 1, UnitPrice = -200 } // Credit used lines are negative
                    }
                }
            };

            // Earned: 1000 (from first invoice)
            // Used: 200 (from third invoice)
            // Available: 800
            Assert.Equal(800m, project.GetAvailableCredit(invoices));
        }

        [Fact]
        public void IsCommissionPayable_ValidatesCorreclty()
        {
            var project = new Project { Status = ProjectStatus.InProduction };
            var invoices = new List<Invoice>
            {
                new Invoice { Status = "Paid" }
            };
            var statuses = new List<ProjectStatusConfig>
            {
                 new ProjectStatusConfig { Name = ProjectStatus.Completed, SystemMapping = SystemStatusMappings.Completed }
            };

            // 1. Not completed
            Assert.False(project.IsCommissionPayable(invoices, statuses));

            // 2. Completed but invoice not paid
            project.Status = ProjectStatus.Completed;
            invoices[0].Status = "Sent";
            Assert.False(project.IsCommissionPayable(invoices, statuses));

            // 3. Completed and all paid
            invoices[0].Status = "Paid";
            Assert.True(project.IsCommissionPayable(invoices, statuses));

            // 4. Completed and some voided
            invoices.Add(new Invoice { Status = "Void" });
            Assert.True(project.IsCommissionPayable(invoices, statuses));
        }

        [Fact]
        public void ProjectStatusConfig_Initialization_SetsDefaults()
        {
            var config = new ProjectStatusConfig();
            Assert.False(string.IsNullOrEmpty(config.Id));
            Assert.Equal(string.Empty, config.Name);
            Assert.Equal("Default", config.Color);
            Assert.False(config.IsSystemDefault);
            Assert.Equal(SystemStatusMappings.Standard, config.SystemMapping);
        }

        [Fact]
        public void ProjectTaskAllocation_Initialization_SetsDefaults()
        {
            var alloc = new ProjectTaskAllocation();
            Assert.False(string.IsNullOrEmpty(alloc.Id));
            Assert.Equal(string.Empty, alloc.WorkTypeId);
            Assert.Equal(string.Empty, alloc.TeamId);
            Assert.Equal(0, alloc.QuotedHours);
            Assert.Equal(string.Empty, alloc.Description);
        }
    }
}


