namespace Spokes_Server.Tests.Core.Services.Accounting;

using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Xunit;

[Collection("JurisdictionStaticState")]
public class JurisdictionTemplateTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IJurisdictionTemplateService _templateService;

    public JurisdictionTemplateTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _templateService = _serviceProvider.GetRequiredService<IJurisdictionTemplateService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void GetAvailableTemplates_LoadsBuiltInJurisdictions_AndSeedsChartOfAccounts()
    {
        var templates = _templateService.GetAvailableTemplates();
        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t.JurisdictionId == "CA-QC");
        Assert.Contains(templates, t => t.JurisdictionId == "CA-ON");
        Assert.Contains(templates, t => t.JurisdictionId == "US-GEN");
        Assert.Contains(templates, t => t.JurisdictionId == "FR-PCG");

        var qc = _templateService.GetTemplateById("CA-QC");
        Assert.NotNull(qc);
        Assert.Equal(2, qc!.SalesTaxes.Count);
        Assert.Contains(qc.SalesTaxes, t => t.Code == "TPS" && t.Rate == 0.05m);
        Assert.Contains(qc.SalesTaxes, t => t.Code == "TVQ" && t.Rate == 0.09975m);

        _templateService.EnsureChartOfAccountsSeeded(qc);
        var accounts = _db.GlAccounts!.GetAll();
        Assert.NotEmpty(accounts);
        Assert.NotNull(_db.GlAccounts.GetBySystemRole(GlSystemRole.OperatingBank));
        Assert.NotNull(_db.GlAccounts.GetBySystemRole(GlSystemRole.AccountsReceivable));
        Assert.NotNull(_db.GlAccounts.GetBySystemRole(GlSystemRole.AccountsPayable));
    }

    [Fact]
    public void ActivateTemplate_SwitchesActiveTaxRulesInFinancialCalculationEngine()
    {
        _templateService.ActivateTemplate("CA-ON", syncChartOfAccounts: true);

        Assert.Equal("CA-ON", _templateService.GetActiveTemplate().JurisdictionId);
        Assert.Equal(0.13m, FinancialCalculationEngine.ActiveCombinedTaxRate);

        var breakdown = FinancialCalculationEngine.CalculateFromSubTotal(1000m, isTaxExempt: false);
        var components = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(1000m, isTaxExempt: false);
        Assert.Equal(1000m, breakdown.SubTotal);
        Assert.Equal(130m, breakdown.TotalTax);
        Assert.Equal(1130m, breakdown.GrandTotal);
        Assert.Single(components);
        Assert.Equal("HST", components[0].Code);

        // Switch back to CA-QC
        _templateService.ActivateTemplate("CA-QC", syncChartOfAccounts: true);
        Assert.Equal(0.14975m, FinancialCalculationEngine.ActiveCombinedTaxRate);
    }

    [Fact]
    public void TemplateMutations_RequireSettingsAccountingAndSalaryPermissions_AndRejectSuspendedOrSystem()
    {
        var acctOnly = new Employee
        {
            Id = "jt-acct",
            IsActive = true,
            Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Business.ManageSettings]
        };
        var suspendedAdmin = new Employee
        {
            Id = "jt-susp-admin",
            IsActive = true,
            IsAdmin = true,
            IsSuspended = true
        };
        var systemActor = new Employee
        {
            Id = "jt-system",
            IsActive = true,
            IsAdmin = true,
            IsSystem = true
        };
        var authorizedAdmin = new Employee
        {
            Id = "jt-admin",
            IsActive = true,
            IsAdmin = true
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            _templateService.ActivateTemplate("CA-ON", syncChartOfAccounts: false, actor: acctOnly));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _templateService.SaveCustomTemplate("{}", actor: suspendedAdmin));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _templateService.ActivateTemplate("CA-ON", syncChartOfAccounts: false, actor: systemActor));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _templateService.EnsureChartOfAccountsSeeded(updateExistingSystemAccounts: false, actor: new Employee
            {
                Id = "jt-no-acct",
                IsActive = true,
                Permissions = [AppPermissions.Business.ManageSettings]
            }));

        _templateService.ActivateTemplate("CA-QC", syncChartOfAccounts: false, actor: authorizedAdmin);
        Assert.Equal("CA-QC", _templateService.GetActiveTemplate().JurisdictionId);
    }

    [Fact]
    public void GetCompanyTaxRegistrations_UsesTemplateLabelOverrides_NormalizesColons_AndFallsBackToJurisdictionDefaults()
    {
        var admin = new Employee { Id = "jt-reg-admin", IsActive = true, IsAdmin = true };
        _templateService.ActivateTemplate("CA-QC", syncChartOfAccounts: false, actor: admin);

        var profile = _db.CompanyProfile.Get();
        profile.Accounting.TaxRegistrationNumbersByCode = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TPS"] = "123456789 RT0001",
            ["TVQ"] = "0987654321 TQ0001"
        };
        _db.CompanyProfile.Save(profile);

        // Without template -> jurisdiction defaults
        var defaultRegs = _templateService.GetCompanyTaxRegistrations();
        Assert.Equal(2, defaultRegs.Count);
        Assert.Equal(("GST / TPS Registration #:", "123456789 RT0001"), defaultRegs[0]);
        Assert.Equal(("QST / TVQ Registration #:", "0987654321 TQ0001"), defaultRegs[1]);

        // Round-trip through System.Text.Json (which produces an ordinal dictionary) + lowercase key + trailing colon
        var rawTemplate = new DocumentTemplate
        {
            TaxRegistrationLabelsByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tps"] = "No de TPS :",
                ["TVQ"] = "   " // blank override should fall back to jurisdiction default
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(rawTemplate);
        var roundTrippedTemplate = System.Text.Json.JsonSerializer.Deserialize<DocumentTemplate>(json)!;

        var localizedRegs = _templateService.GetCompanyTaxRegistrations(roundTrippedTemplate);
        Assert.Equal(2, localizedRegs.Count);
        Assert.Equal(("No de TPS:", "123456789 RT0001"), localizedRegs[0]);
        Assert.Equal(("QST / TVQ Registration #:", "0987654321 TQ0001"), localizedRegs[1]);
    }
}
