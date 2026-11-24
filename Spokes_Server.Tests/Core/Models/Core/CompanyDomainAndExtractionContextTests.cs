using System.Text;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Tests;

namespace Spokes_Server.Tests.Core.Models.Core;

public class CompanyDomainAndExtractionContextTests
{
    [Theory]
    [InlineData("https://www.acme.com/contact", "acme.com")]
    [InlineData("acme-robotics.ca", "acme-robotics.ca")]
    [InlineData("HTTP://Sub.Example.ORG.", "sub.example.org")]
    [InlineData("http://localhost:5000", null)]
    [InlineData("https://192.168.1.50:8443", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractDomainFromUrl_NormalizesAndFiltersInvalidHosts(string? input, string? expected)
    {
        Assert.Equal(expected, CompanyProfile.ExtractDomainFromUrl(input));
    }

    [Theory]
    [InlineData("factures@acme.com", "acme.com")]
    [InlineData("@Acme.COM", "acme.com")]
    [InlineData("acme.io", "acme.io")]
    [InlineData("invalid", null)]
    [InlineData("", null)]
    public void ExtractDomainFromEmail_NormalizesAddressesAndDomains(string? input, string? expected)
    {
        Assert.Equal(expected, CompanyProfile.ExtractDomainFromEmail(input));
    }

    [Theory]
    [InlineData("gmail.com", true)]
    [InlineData("outlook.fr", true)]
    [InlineData("icloud.com", true)]
    [InlineData("yahoo.ca", true)]
    [InlineData("acme.com", false)]
    [InlineData("spokes.sh", false)]
    public void IsPublicMailboxDomain_DistinguishesConsumerProvidersFromCustomDomains(string domain, bool expected)
    {
        Assert.Equal(expected, EmailProviderPresets.IsPublicMailboxDomain(domain));
    }

    [Fact]
    public void GetPrimaryDomainOrDefault_FallsBackToExampleCom_AndSkipsPublicMailboxDomains()
    {
        var profile = new CompanyProfile();
        profile.EmailSettings.IncomingServers.Add(new IncomingServerConfig { Domain = "gmail.com" });
        profile.Accounting.BillInbox.EmailAddress = "bills@outlook.com";

        Assert.Empty(profile.GetInternalDomains());
        Assert.Equal("example.com", profile.GetPrimaryDomainOrDefault());

        profile.Website = "https://www.northstar.io";
        profile.EmailSettings.SendingServers.Add(new SendingServerConfig { Domain = "@mail.northstar.io" });

        var domains = profile.GetInternalDomains();
        Assert.Equal(["northstar.io", "mail.northstar.io"], domains);
        Assert.Equal("northstar.io", profile.GetPrimaryDomainOrDefault());
        Assert.True(profile.IsInternalDomain("sub.northstar.io"));
        Assert.False(profile.IsInternalDomain("gmail.com"));
    }

    [Fact]
    public void InvoiceExtractionContext_DefaultHasNoHardcodedCompany_AndAllowsExternalVendorAsSupplier()
    {
        var defaultCtx = new InvoiceExtractionContext();
        Assert.Equal(string.Empty, defaultCtx.BuyerCompanyName);
        Assert.Empty(defaultCtx.BuyerAliases);
        Assert.Empty(defaultCtx.BuyerEmailDomains);

        var sb = new StringBuilder();
        sb.AppendLine("INVOICE");
        sb.AppendLine("Apex Automation Inc. | Bill to: Northstar Manufacturing Ltd.");
        sb.AppendLine("billing@apex-auto.test | ap@northstar.io");
        sb.AppendLine("Total: 250.00");

        var buyerProfile = new CompanyProfile
        {
            CompanyName = "Northstar-Manufacturing Ltd.",
            Website = "https://northstar.io"
        };
        var ctx = InvoiceExtractionContext.FromCompanyProfile(buyerProfile);

        Assert.Contains("Northstar Manufacturing", ctx.BuyerNames);
        Assert.Contains("NorthstarManufacturing", ctx.BuyerNames);
        Assert.Contains("northstar.io", ctx.AllBuyerEmailDomains);

        var data = new ExtractedInvoiceData();
        UniversalPartyExtractor.ExtractParties(SpatialTextDocumentTestHelper.Create(sb.ToString()), "invoice.pdf", data, ctx);

        Assert.Equal("Apex Automation Inc.", data.SupplierName);
        Assert.Equal("billing@apex-auto.test", data.SupplierEmail);
    }

    [Fact]
    public void InvoiceExtractionContext_FromDefaultMySpokesProfile_LeavesBuyerCompanyNameEmpty()
    {
        var unconfiguredProfile = new CompanyProfile();
        var ctx = InvoiceExtractionContext.FromCompanyProfile(unconfiguredProfile);

        Assert.Equal(string.Empty, ctx.BuyerCompanyName);
        Assert.Empty(ctx.BuyerNames);
    }
}
