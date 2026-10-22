using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates historical invoices with legacy AmountPaid into canonical PaymentRecord entries in Payments.
/// </summary>
public class InvoicePaymentMigrationStep : IMigrationStep
{
    private readonly AccountingContext _accounting;
    private readonly ILogger<InvoicePaymentMigrationStep>? _logger;

    public string MigrationKey => "20260929_InvoiceLegacyPaymentToPaymentRecord";
    public string Description => "Migrates historical invoices with legacy AmountPaid to canonical PaymentRecord entries in Payments";
    public int Order => 45;

    [ActivatorUtilitiesConstructor]
    public InvoicePaymentMigrationStep(AccountingContext accountingContext, ILogger<InvoicePaymentMigrationStep>? logger = null)
    {
        _accounting = accountingContext;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migrated = 0;

        foreach (var inv in _accounting.Invoices.GetAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Invoices with legacy AmountPaid are automatically converted into a PaymentRecord
            // in Payments during JSON deserialization via [JsonOnDeserialized]. Saving them persists the canonical structure.
            if (inv.Payments.Any(p => p.Method == "Legacy"))
            {
                _accounting.Invoices.Save(inv);
                migrated++;
            }
        }

        if (migrated > 0)
        {
            _logger?.LogInformation("[InvoicePaymentMigrationStep] Persisted {Count} invoice(s) migrated to canonical PaymentRecord.", migrated);
        }

        return Task.CompletedTask;
    }
}
