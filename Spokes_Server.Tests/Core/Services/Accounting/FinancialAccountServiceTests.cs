namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Xunit;

[Collection("JurisdictionStaticState")]
public class FinancialAccountServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IFinancialAccountService _financialAccounts;
    private readonly IDirectTransactionService _directTransactions;
    private readonly ILedgerService _ledgerService;

    private static readonly Employee AdminActor = new()
    {
        Id = "fa-admin",
        FirstName = "Finance",
        LastName = "Owner",
        IsActive = true,
        IsAdmin = true
    };

    public FinancialAccountServiceTests()
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
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<IDirectTransactionService, DirectTransactionService>();
        services.AddScoped<IFinancialAccountService, FinancialAccountService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _ledgerService = _serviceProvider.GetRequiredService<ILedgerService>();
        _directTransactions = _serviceProvider.GetRequiredService<IDirectTransactionService>();
        _financialAccounts = _serviceProvider.GetRequiredService<IFinancialAccountService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void EnsureBootstrapped_FreshWorkspace_SeedsOnlyPrimaryOperatingAccount()
    {
        var active = _financialAccounts.GetActiveAccounts();

        Assert.Single(active);
        var operating = active[0];
        Assert.Equal(FinancialAccountType.Chequing, operating.AccountType);
        Assert.True(operating.IsDefaultOperating);
        Assert.Equal("1020", operating.LinkedGlAccountCode);
    }

    [Fact]
    public void CreateAccount_AllTypes_AutoCreatesDedicatedGlAccountsInExpectedRanges()
    {
        _financialAccounts.EnsureBootstrapped();

        var savings = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Tax Reserve Savings",
            AccountType = FinancialAccountType.Savings,
            Currency = "CAD",
            InstitutionName = "Desjardins",
            Last4Digits = "1111"
        });
        var card = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "RBC Visa Corporate",
            AccountType = FinancialAccountType.CreditCard,
            Currency = "CAD",
            CreditLimit = 25000m,
            Last4Digits = "4242"
        });
        var loc = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "BNC Standalone LOC",
            AccountType = FinancialAccountType.LineOfCredit,
            Currency = "CAD",
            CreditLimit = 50000m
        });
        var processor = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Stripe CAD",
            AccountType = FinancialAccountType.PaymentProcessor,
            Currency = "CAD"
        });
        var loan = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Equipment Term Loan",
            AccountType = FinancialAccountType.Loan,
            Currency = "CAD"
        });
        var cash = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Office Petty Cash",
            AccountType = FinancialAccountType.CashOnHand,
            Currency = "CAD"
        });

        Assert.True(savings.IsSuccess);
        Assert.True(card.IsSuccess);
        Assert.True(loc.IsSuccess);
        Assert.True(processor.IsSuccess);
        Assert.True(loan.IsSuccess);
        Assert.True(cash.IsSuccess);

        // Verify backing GL accounts were created with proper types/subtypes
        var locGl = _db.GlAccounts.GetByCode(loc.Value!.LinkedGlAccountCode);
        Assert.NotNull(locGl);
        Assert.Equal(GlAccountType.Liability, locGl!.Type);
        Assert.Equal(GlAccountSubType.LineOfCredit, locGl.SubType);

        var loanGl = _db.GlAccounts.GetByCode(loan.Value!.LinkedGlAccountCode);
        Assert.NotNull(loanGl);
        Assert.Equal(GlAccountType.Liability, loanGl!.Type);
        Assert.Equal(GlAccountSubType.Loan, loanGl.SubType);

        // First credit card auto-promotes to default credit card
        Assert.True(card.Value!.IsDefaultCreditCard);
        Assert.Equal(card.Value.LinkedGlAccountCode, _financialAccounts.ResolveDefaultGlCodeForMethod("Credit Card"));
        Assert.Equal(cash.Value!.LinkedGlAccountCode, _financialAccounts.ResolveDefaultGlCodeForMethod("Cash"));

        // Account -> default payment method resolution
        Assert.Equal("Wire Transfer", _financialAccounts.ResolveDefaultPaymentMethodForAccount(savings.Value!.LinkedGlAccountCode, "Wire Transfer"));
        Assert.Equal("EFT / Wire", _financialAccounts.ResolveDefaultPaymentMethodForAccount(loc.Value!.LinkedGlAccountCode, "EFT / Wire"));
        Assert.Equal("Credit Card", _financialAccounts.ResolveDefaultPaymentMethodForAccount(card.Value.LinkedGlAccountCode, "Wire Transfer"));
        Assert.Equal("Cash", _financialAccounts.ResolveDefaultPaymentMethodForAccount(cash.Value.LinkedGlAccountCode, "Wire Transfer"));
        Assert.Equal("Credit Card", _financialAccounts.ResolveDefaultPaymentMethodForAccount(processor.Value!.LinkedGlAccountCode, "Wire Transfer"));

        // Friendly display label never exposes GL codes
        var cardLabel = _financialAccounts.GetDisplayLabelByGlCode(card.Value.LinkedGlAccountCode);
        Assert.Contains("RBC Visa Corporate", cardLabel);
        Assert.Contains("4242", cardLabel);
        Assert.DoesNotContain(card.Value.LinkedGlAccountCode, cardLabel);
    }

    [Fact]
    public void CreateAccount_ManualGlLink_LinksExistingAndRejectsDuplicateLink()
    {
        _financialAccounts.EnsureBootstrapped();

        // 1030 is seeded by default template and not yet linked
        var first = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "USD Operations",
            AccountType = FinancialAccountType.Savings,
            Currency = "USD"
        }, existingGlAccountCode: "1030");

        Assert.True(first.IsSuccess);
        Assert.Equal("1030", first.Value!.LinkedGlAccountCode);

        // Attempting to link a second active account to 1030 must fail
        var duplicate = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Another Savings",
            AccountType = FinancialAccountType.Savings,
            Currency = "CAD"
        }, existingGlAccountCode: "1030");

        Assert.False(duplicate.IsSuccess);
    }

    [Fact]
    public void AttachCreditLineToChequing_AllowsNegativeBalanceUpToLimitWithoutTransfers()
    {
        var operating = _financialAccounts.GetActiveAccounts().Single();

        var attachResult = _financialAccounts.AttachCreditLineToChequing(operating.Id, 100000m, "Operating credit line");
        Assert.True(attachResult.IsSuccess);
        Assert.True(attachResult.Value!.HasAttachedCreditLine);
        Assert.Equal(100000m, attachResult.Value.CreditLimit);

        // Post a $99,000 MoneyOut directly from the operating bank account (taking it to -$99,000)
        var tx = new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyOut,
            Date = DateTime.Today,
            AccountGlCode = operating.LinkedGlAccountCode,
            PartyName = "Major Supplier",
            Description = "Equipment purchase drawn on operating credit line",
            TaxMode = DirectTransactionTaxMode.NoTax,
            Lines = [new DirectTransactionLine { GlAccountCode = "5600", EnteredAmount = 99000m }]
        };
        _directTransactions.SaveTransaction(tx, AdminActor);

        var balances = _financialAccounts.GetLiveBalances(DateTime.Today);
        Assert.Equal(-99000m, balances[operating.Id]);

        // Balance sheet still balances cleanly with negative bank balance
        var bs = _ledgerService.CalculateBalanceSheet(DateTime.Today);
        Assert.True(bs.IsBalanced);
    }

    [Fact]
    public void StandaloneLineOfCredit_TransferToChequing_PostsBalancedJournalEntry()
    {
        var operating = _financialAccounts.GetActiveAccounts().Single();
        var loc = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "BNC Standalone LOC",
            AccountType = FinancialAccountType.LineOfCredit,
            Currency = "CAD",
            CreditLimit = 75000m
        }).Value!;

        // Draw $20,000 from standalone LOC into Operating Chequing
        var transfer = new DirectTransaction
        {
            Direction = DirectTransactionDirection.Transfer,
            Date = DateTime.Today,
            AccountGlCode = loc.LinkedGlAccountCode,
            DestinationAccountGlCode = operating.LinkedGlAccountCode,
            GrandTotal = 20000m,
            Description = "Draw from line of credit"
        };
        _directTransactions.SaveTransaction(transfer, AdminActor);

        var balances = _financialAccounts.GetLiveBalances(DateTime.Today);
        Assert.Equal(20000m, balances[operating.Id]);
        Assert.Equal(20000m, balances[loc.Id]); // Liability normal credit balance = $20,000 owing
    }

    [Fact]
    public void UpdateAndArchive_SyncsGlAccountAndProtectsLastActiveAccount()
    {
        var operating = _financialAccounts.GetActiveAccounts().Single();

        // Cannot archive the only active account
        var blockedArchive = _financialAccounts.ArchiveAccount(operating.Id);
        Assert.False(blockedArchive.IsSuccess);

        // Add a second chequing account and make it default
        var second = _financialAccounts.CreateAccount(new FinancialAccount
        {
            Name = "Desjardins Main",
            AccountType = FinancialAccountType.Chequing,
            Currency = "USD",
            IsDefaultOperating = true
        }).Value!;

        // Default exclusivity updated
        var reloadedOriginal = _financialAccounts.GetById(operating.Id)!;
        Assert.False(reloadedOriginal.IsDefaultOperating);
        Assert.True(second.IsDefaultOperating);

        // Rename & currency syncs to backing GL account
        second.Name = "Desjardins Main USD";
        second.Currency = "USD";
        var updated = _financialAccounts.UpdateAccount(second);
        Assert.True(updated.IsSuccess);
        var backingGl = _db.GlAccounts.GetByCode(second.LinkedGlAccountCode);
        Assert.NotNull(backingGl);
        Assert.Equal("Desjardins Main USD", backingGl!.Name);
        Assert.Equal("USD", backingGl.Currency);

        // Archiving original now succeeds and hides it from active list
        var archived = _financialAccounts.ArchiveAccount(operating.Id);
        Assert.True(archived.IsSuccess);
        Assert.DoesNotContain(_financialAccounts.GetActiveAccounts(), a => a.Id == operating.Id);

        // Restoring brings it back
        var restored = _financialAccounts.RestoreAccount(operating.Id);
        Assert.True(restored.IsSuccess);
        Assert.Contains(_financialAccounts.GetActiveAccounts(), a => a.Id == operating.Id);
    }
}
