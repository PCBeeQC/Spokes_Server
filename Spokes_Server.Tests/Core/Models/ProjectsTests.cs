using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;

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

        #region Equals and GetHashCode

        [Fact]
        public void Project_Equals_HandlesReferenceEquality()
        {
            var p = new Project { Id = "P-1" };
            Assert.True(p.Equals(p));
            Assert.True(p.Equals((object)p));
        }

        [Fact]
        public void Project_Equals_SameId_ReturnsTrue()
        {
            var p1 = new Project { Id = "P-1", Name = "Project 1" };
            var p2 = new Project { Id = "P-1", Name = "Different Name" };
            Assert.True(p1.Equals(p2));
            Assert.True(p1.Equals((object)p2));
        }

        [Fact]
        public void Project_Equals_DifferentId_ReturnsFalse()
        {
            var p1 = new Project { Id = "P-1" };
            var p2 = new Project { Id = "P-2" };
            Assert.False(p1.Equals(p2));
        }

        [Fact]
        public void Project_Equals_NullOrDifferentType_ReturnsFalse()
        {
            var p = new Project { Id = "P-1" };
            Assert.False(p.Equals(null));
            Assert.False(p.Equals("string object"));
            Assert.False(p.Equals(123));
            Assert.False(p.Equals(new Invoice { Id = "P-1" }));
        }

        [Fact]
        public void Project_GetHashCode_SameId_ProducesSameHashCode()
        {
            var p1 = new Project { Id = "P-1" };
            var p2 = new Project { Id = "P-1" };
            Assert.Equal(p1.GetHashCode(), p2.GetHashCode());
        }

        [Fact]
        public void Project_GetHashCode_NullId_DoesNotThrow()
        {
            var p = new Project { Id = null! };
            var exception = Record.Exception(() => p.GetHashCode());
            Assert.Null(exception);
        }

        #endregion

        #region Constants and Lists

        [Fact]
        public void BillingMethods_Constants_HaveExpectedValues()
        {
            Assert.Equal("Time & Materials (Cost+)", BillingMethods.TimeAndMaterials);
            Assert.Equal("Fixed Price / Milestones", BillingMethods.FixedPrice);
        }

        [Fact]
        public void ProjectStatus_ConstantsAndAllList_AreValid()
        {
            Assert.Equal("Draft", ProjectStatus.Draft);
            Assert.Equal("Quoted", ProjectStatus.Quoted);
            Assert.Equal("Quote Accepted / In Production", ProjectStatus.InProduction);
            Assert.Equal("Waiting for last payment", ProjectStatus.WaitingForPayment);
            Assert.Equal("Discovery", ProjectStatus.Discovery);
            Assert.Equal("On Hold", ProjectStatus.OnHold);
            Assert.Equal("Completed", ProjectStatus.Completed);
            Assert.Equal("Archived", ProjectStatus.Archived);

            var all = ProjectStatus.All;
            Assert.Equal(8, all.Count);
            Assert.Contains(ProjectStatus.Draft, all);
            Assert.Contains(ProjectStatus.Quoted, all);
            Assert.Contains(ProjectStatus.InProduction, all);
            Assert.Contains(ProjectStatus.WaitingForPayment, all);
            Assert.Contains(ProjectStatus.Discovery, all);
            Assert.Contains(ProjectStatus.OnHold, all);
            Assert.Contains(ProjectStatus.Completed, all);
            Assert.Contains(ProjectStatus.Archived, all);
        }

        [Fact]
        public void SystemStatusMappings_ConstantsAndAllList_AreValid()
        {
            Assert.Equal("Standard", SystemStatusMappings.Standard);
            Assert.Equal("Completed", SystemStatusMappings.Completed);
            Assert.Equal("Archived", SystemStatusMappings.Archived);

            var all = SystemStatusMappings.All;
            Assert.Equal(3, all.Count);
            Assert.Contains(SystemStatusMappings.Standard, all);
            Assert.Contains(SystemStatusMappings.Completed, all);
            Assert.Contains(SystemStatusMappings.Archived, all);
        }

        #endregion

        #region Property Mutations

        [Fact]
        public void Project_Properties_CanBeMutated()
        {
            var project = new Project();
            var now = DateTime.UtcNow;
            var client = new ClientInfo { BusinessName = "ACME Corp" };
            var commission = new CommissionBeneficiary { EmployeeId = "emp-1", Percentage = 10m };
            var allocation = new ProjectTaskAllocation { WorkTypeId = "WT-1", QuotedHours = 20m };
            var po = new ClientPO { PoNumber = "PO-999" };

            project.Id = "PROJ-100";
            project.ProjectNumber = "PRJ-001";
            project.Name = "Alpha";
            project.Client = client;
            project.Description = "A new project";
            project.Status = ProjectStatus.InProduction;
            project.RateCardId = "RC-1";
            project.CustomRates = new Dictionary<string, decimal> { ["WT-1"] = 175m };
            project.IsInternal = true;
            project.IsActive = false;
            project.CreatedAt = now;
            project.LastEdited = now.AddHours(1);
            project.BillingMethod = BillingMethods.FixedPrice;
            project.DefaultTemplateId = "TMPL-1";
            project.ProjectManagerId = "PM-1";
            project.Commissions = new List<CommissionBeneficiary> { commission };
            project.AccessPolicy = "Restricted";
            project.AllowedTeamIds = new List<string> { "team-1" };
            project.AllowedUserIds = new List<string> { "user-2" };
            project.ApprovedWorkTypeIds = new List<string> { "wt-dev" };
            project.Allocations = new List<ProjectTaskAllocation> { allocation };
            project.PurchaseOrders = new List<ClientPO> { po };
            project.NoteCategories = new List<string> { "CustomCat" };
            project.NoteCategoryColors = new Dictionary<string, string> { ["CustomCat"] = "Primary" };
            project.ProjectGroupId = "GRP-1";

            Assert.Equal("PROJ-100", project.Id);
            Assert.Equal("PRJ-001", project.ProjectNumber);
            Assert.Equal("Alpha", project.Name);
            Assert.Equal("PRJ-001 - Alpha", project.DisplayName);
            Assert.Same(client, project.Client);
            Assert.Equal("A new project", project.Description);
            Assert.Equal(ProjectStatus.InProduction, project.Status);
            Assert.Equal("RC-1", project.RateCardId);
            Assert.Equal(175m, project.CustomRates["WT-1"]);
            Assert.True(project.IsInternal);
            Assert.False(project.IsActive);
            Assert.Equal(now, project.CreatedAt);
            Assert.Equal(now.AddHours(1), project.LastEdited);
            Assert.Equal(BillingMethods.FixedPrice, project.BillingMethod);
            Assert.Equal("TMPL-1", project.DefaultTemplateId);
            Assert.Equal("PM-1", project.ProjectManagerId);
            Assert.Single(project.Commissions);
            Assert.Equal("Restricted", project.AccessPolicy);
            Assert.Contains("team-1", project.AllowedTeamIds);
            Assert.Contains("user-2", project.AllowedUserIds);
            Assert.Contains("wt-dev", project.ApprovedWorkTypeIds);
            Assert.Single(project.Allocations);
            Assert.Single(project.PurchaseOrders);
            Assert.Contains("CustomCat", project.NoteCategories);
            Assert.Equal("Primary", project.NoteCategoryColors["CustomCat"]);
            Assert.Equal("GRP-1", project.ProjectGroupId);
        }

        [Fact]
        public void ProjectStatusConfig_Properties_CanBeMutated()
        {
            var config = new ProjectStatusConfig
            {
                Id = "CFG-1",
                Name = "Under Review",
                Color = "Warning",
                IsSystemDefault = true,
                SystemMapping = SystemStatusMappings.Completed
            };

            Assert.Equal("CFG-1", config.Id);
            Assert.Equal("Under Review", config.Name);
            Assert.Equal("Warning", config.Color);
            Assert.True(config.IsSystemDefault);
            Assert.Equal(SystemStatusMappings.Completed, config.SystemMapping);
        }

        [Fact]
        public void ProjectTaskAllocation_Properties_CanBeMutated()
        {
            var alloc = new ProjectTaskAllocation
            {
                Id = "ALLOC-1",
                WorkTypeId = "WT-DESIGN",
                TeamId = "TEAM-DESIGN",
                QuotedHours = 42.5m,
                Description = "Design work"
            };

            Assert.Equal("ALLOC-1", alloc.Id);
            Assert.Equal("WT-DESIGN", alloc.WorkTypeId);
            Assert.Equal("TEAM-DESIGN", alloc.TeamId);
            Assert.Equal(42.5m, alloc.QuotedHours);
            Assert.Equal("Design work", alloc.Description);
        }

        #endregion

        #region GetEffectiveRate

        [Fact]
        public void GetEffectiveRate_FallbacksWorkAsExpected()
        {
            var project = new Project();
            decimal defaultRate = 75m;

            // 1. CustomRates doesn't contain key and rateCard is null -> returns defaultSystemRate
            var rate1 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, null);
            Assert.Equal(defaultRate, rate1);

            // 2. CustomRates doesn't contain key and rateCard is not null, but rateCard.Rates doesn't contain key -> returns defaultSystemRate
            var rateCard = new RateCard();
            rateCard.Rates["OtherWorkType"] = 120m;
            var rate2 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, rateCard);
            Assert.Equal(defaultRate, rate2);

            // 3. CustomRates is null fallback -> does not throw, returns rateCard or default
            project.CustomRates = null!;
            var rate3 = project.GetEffectiveRate("NonExistentWorkType", defaultRate, null);
            Assert.Equal(defaultRate, rate3);

            var rate4 = project.GetEffectiveRate("OtherWorkType", defaultRate, rateCard);
            Assert.Equal(120m, rate4);
        }

        #endregion

        #region GetAvailableCredit

        [Fact]
        public void GetAvailableCredit_EmptyInvoiceList_ReturnsZero()
        {
            var project = new Project();
            var credit = project.GetAvailableCredit(new List<Invoice>());
            Assert.Equal(0m, credit);
        }

        [Fact]
        public void GetAvailableCredit_InvoicesWithNoPrepaymentsOrCreditUsed_ReturnsZero()
        {
            var project = new Project();
            var invoices = new List<Invoice>
            {
                new Invoice
                {
                    Status = "Paid",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = false, IsCreditUsed = false, Quantity = 2, UnitPrice = 150m }
                    }
                },
                new Invoice
                {
                    Status = "Sent",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = false, IsCreditUsed = false, Quantity = 1, UnitPrice = 500m }
                    }
                }
            };

            var credit = project.GetAvailableCredit(invoices);
            Assert.Equal(0m, credit);
        }

        [Fact]
        public void GetAvailableCredit_CalculatesPositiveAndNegativeCredit()
        {
            var project = new Project();

            // Case 1: Positive credit
            var positiveInvoices = new List<Invoice>
            {
                new Invoice
                {
                    Status = "Paid",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = true, Quantity = 1, UnitPrice = 500m }
                    }
                },
                new Invoice
                {
                    Status = "Draft",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsCreditUsed = true, Quantity = 1, UnitPrice = -150m }
                    }
                }
            };
            Assert.Equal(350m, project.GetAvailableCredit(positiveInvoices));

            // Case 2: Negative credit (credit used exceeds credit earned)
            var negativeInvoices = new List<Invoice>
            {
                new Invoice
                {
                    Status = "Paid",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = true, Quantity = 1, UnitPrice = 100m }
                    }
                },
                new Invoice
                {
                    Status = "Sent",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsCreditUsed = true, Quantity = 1, UnitPrice = -250m }
                    }
                }
            };
            Assert.Equal(-150m, project.GetAvailableCredit(negativeInvoices));

            // Case 3: Void invoices are ignored for credit used, and unpaid/void ignored for credit earned
            var voidInvoices = new List<Invoice>
            {
                new Invoice
                {
                    Status = "Void",
                    Lines = new List<InvoiceLine>
                    {
                        new InvoiceLine { IsPrepayment = true, Quantity = 1, UnitPrice = 1000m },
                        new InvoiceLine { IsCreditUsed = true, Quantity = 1, UnitPrice = -400m }
                    }
                }
            };
            Assert.Equal(0m, project.GetAvailableCredit(voidInvoices));
        }

        #endregion

        #region IsCommissionPayable

        [Fact]
        public void IsCommissionPayable_StatusMatchingArchived_ReturnsTrue()
        {
            var project = new Project { Status = ProjectStatus.Archived };
            var invoices = new List<Invoice>
            {
                new Invoice { Status = "Paid" }
            };
            var statuses = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Name = ProjectStatus.Archived, SystemMapping = SystemStatusMappings.Archived }
            };

            Assert.True(project.IsCommissionPayable(invoices, statuses));
        }

        [Fact]
        public void IsCommissionPayable_CaseInsensitiveStatusMatching_ReturnsTrue()
        {
            var project = new Project { Status = "completed" }; // lowercase
            var invoices = new List<Invoice>
            {
                new Invoice { Status = "Paid" }
            };
            var statuses = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Name = "Completed", SystemMapping = SystemStatusMappings.Completed }
            };

            Assert.True(project.IsCommissionPayable(invoices, statuses));

            project.Status = "ARCHIVED"; // uppercase
            statuses[0] = new ProjectStatusConfig { Name = "Archived", SystemMapping = SystemStatusMappings.Archived };
            Assert.True(project.IsCommissionPayable(invoices, statuses));
        }

        [Fact]
        public void IsCommissionPayable_MissingOrNullStatusConfig_ReturnsFalse()
        {
            var project = new Project { Status = ProjectStatus.Completed };
            var invoices = new List<Invoice> { new Invoice { Status = "Paid" } };

            // Empty statuses list
            Assert.False(project.IsCommissionPayable(invoices, new List<ProjectStatusConfig>()));

            // Status not found in configuration
            var otherStatuses = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Name = "In Review", SystemMapping = SystemStatusMappings.Standard }
            };
            Assert.False(project.IsCommissionPayable(invoices, otherStatuses));

            // Status found but SystemMapping is Standard (not Completed or Archived)
            var standardStatus = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Name = ProjectStatus.Completed, SystemMapping = SystemStatusMappings.Standard }
            };
            Assert.False(project.IsCommissionPayable(invoices, standardStatus));
        }

        [Fact]
        public void IsCommissionPayable_InvoiceStatuses_HandlesVoidAndUnacceptedStatuses()
        {
            var project = new Project { Status = ProjectStatus.Completed };
            var statuses = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Name = ProjectStatus.Completed, SystemMapping = SystemStatusMappings.Completed }
            };

            // All Void invoices -> returns true
            var allVoidInvoices = new List<Invoice>
            {
                new Invoice { Status = "Void" },
                new Invoice { Status = "Void" }
            };
            Assert.True(project.IsCommissionPayable(allVoidInvoices, statuses));

            // Mix of Paid and Void invoices -> returns true
            var paidAndVoidInvoices = new List<Invoice>
            {
                new Invoice { Status = "Paid" },
                new Invoice { Status = "Void" }
            };
            Assert.True(project.IsCommissionPayable(paidAndVoidInvoices, statuses));

            // Unaccepted status: Sent -> returns false
            var sentInvoice = new List<Invoice>
            {
                new Invoice { Status = "Sent" }
            };
            Assert.False(project.IsCommissionPayable(sentInvoice, statuses));

            // Unaccepted status: Draft -> returns false
            var draftInvoice = new List<Invoice>
            {
                new Invoice { Status = "Draft" }
            };
            Assert.False(project.IsCommissionPayable(draftInvoice, statuses));

            // Empty invoice list -> allInvoicesPaid evaluates to true on empty sequence
            var emptyInvoices = new List<Invoice>();
            Assert.True(project.IsCommissionPayable(emptyInvoices, statuses));
        }

        #endregion

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
