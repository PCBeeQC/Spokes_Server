using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class SmartPoMatchingEngineTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly SmartPoMatchingEngine _matchingEngine;

    public SmartPoMatchingEngineTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _matchingEngine = new SmartPoMatchingEngine(_db);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void FindMatch_ExplicitPoNumber_MatchesDirectly()
    {
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid().ToString(),
            PoNumber = "PO-2026-0001",
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "Robot Parts Co" },
            LineItems = [new PoItem { Description = "Bearing", Quantity = 2, UnitPrice = 50m }]
        };
        _db.PurchaseOrders.Save(po);

        var invoice = new ExtractedInvoiceData
        {
            PurchaseOrderReference = "PO-2026-0001",
            TotalAmount = 100m
        };

        var result = _matchingEngine.FindMatch(invoice);

        Assert.NotNull(result.MatchedPo);
        Assert.Equal(po.Id, result.MatchedPo.Id);
        Assert.Equal("High", result.Confidence);
        Assert.Contains("explicit invoice order reference", result.Reason);
    }

    [Fact]
    public void FindMatch_PoNumberInDescription_MatchesHeuristically()
    {
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid().ToString(),
            PoNumber = "PO-2026-0088",
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "Fasteners Inc" },
            LineItems = [new PoItem { Description = "Screws", Quantity = 100, UnitPrice = 0.5m }]
        };
        _db.PurchaseOrders.Save(po);

        var invoice = new ExtractedInvoiceData
        {
            InvoiceNumber = "INV-PO-2026-0088",
            TotalAmount = 50m
        };

        var result = _matchingEngine.FindMatch(invoice);

        Assert.NotNull(result.MatchedPo);
        Assert.Equal(po.Id, result.MatchedPo.Id);
        Assert.Equal("High", result.Confidence);
        Assert.Contains("referenced in invoice number", result.Reason);
    }

    [Fact]
    public void FindMatch_SupplierAndExactAmount_MatchesOpenPo()
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid().ToString()
        };
        supplier.ContactInfo.BusinessName = "ElectroCorp";
        _db.Suppliers.Save(supplier);

        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid().ToString(),
            PoNumber = "PO-2026-0099",
            ApplyTax = false,
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "ElectroCorp" },
            LineItems = [new PoItem { Description = "Capacitors", Quantity = 10, UnitPrice = 45m }]
        };
        _db.PurchaseOrders.Save(po);

        var invoice = new ExtractedInvoiceData
        {
            SupplierName = "ElectroCorp",
            TotalAmount = 450m
        };

        var result = _matchingEngine.FindMatch(invoice, supplier.Id);

        Assert.NotNull(result.MatchedPo);
        Assert.Equal(po.Id, result.MatchedPo.Id);
        Assert.Equal("High", result.Confidence);
        Assert.Contains("exact total", result.Reason);
    }

    [Fact]
    public void FindMatch_NoOpenPoMatches_ReturnsNull()
    {
        var invoice = new ExtractedInvoiceData
        {
            SupplierName = "Unknown Corp",
            TotalAmount = 9999m
        };

        var result = _matchingEngine.FindMatch(invoice);

        Assert.Null(result.MatchedPo);
        Assert.Equal("None", result.Confidence);
        Assert.Contains("No matching Purchase Order found", result.Reason);
    }
}
