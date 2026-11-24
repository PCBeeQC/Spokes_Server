namespace Spokes_Server.Core.Data.Repositories.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Accounting;

public class SuggestedInvoicePaymentRepository : JsonRepository<SuggestedInvoicePayment>
{
    public SuggestedInvoicePaymentRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "SuggestedInvoicePayments"), "*.json")
    {
    }

    protected override string GetFilePath(SuggestedInvoicePayment item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<SuggestedInvoicePayment> GetPending() =>
        GetAll()
            .Where(s => string.Equals(s.Status, SuggestedInvoicePaymentStatus.Pending, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToList();

    public bool ExistsForEmailAndInvoice(string sourceEmailId, string invoiceId) =>
        GetAll().Any(s =>
            string.Equals(s.SourceEmailId, sourceEmailId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.InvoiceId, invoiceId, StringComparison.OrdinalIgnoreCase));
}
