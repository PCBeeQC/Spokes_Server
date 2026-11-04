namespace Spokes_Server.Core.Services.Accounting.Export;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Accounting;

public class Sage50ExportService : ISage50ExportService
{
    private readonly Database _db;

    public Sage50ExportService(Database db)
    {
        _db = db;
    }

    // =========================================================================
    // 1. LEGACY / DIRECT BILL OVERLOADS (Delegates to Unified Pipeline)
    // =========================================================================

    public SageExportValidationResult ValidateForExport(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, Sage50ExportConfig config)
    {
        var payables = (bills ?? []).Select(b => ConvertBillToExportItem(b, config)).ToList();
        return ValidateForExport(invoices, payables, config);
    }

    public string GenerateSage50Imp(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, Sage50ExportConfig config)
    {
        var payables = (bills ?? []).Select(b => ConvertBillToExportItem(b, config)).ToList();
        return GenerateSage50Imp(invoices, payables, config);
    }

    public string GeneratePurchaseInvoicesCsv(IEnumerable<Bill> bills, Sage50ExportConfig config)
    {
        var payables = (bills ?? []).Select(b => ConvertBillToExportItem(b, config)).ToList();
        return GeneratePurchaseInvoicesCsv(payables, config);
    }

    public void MarkAsExported(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, string batchId)
    {
        var payables = (bills ?? []).Select(b => ConvertBillToExportItem(b)).ToList();
        MarkAsExported(invoices, payables, batchId, overwriteExistingBatch: true);
    }

    public void UnmarkExported(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills)
    {
        var payables = (bills ?? []).Select(b => ConvertBillToExportItem(b)).ToList();
        UnmarkExported(invoices, payables);
    }

    // =========================================================================
    // 2. UNIFIED PAYABLE AGGREGATION (Bills, Recurring Bills, Expenses, Commissions)
    // =========================================================================

    public List<SagePayableExportItem> GetAllPayableExportItems()
    {
        var config = _db.CompanyProfile.GetAccounting().SageSettings ?? new Sage50ExportConfig();
        var items = new List<SagePayableExportItem>();

        // 1. Supplier Bills & PO Bills
        var bills = _db.Bills.GetAll()
            .Where(b => b.Status != BillStatus.Void && b.Status != BillStatus.AwaitingApproval);
        foreach (var bill in bills)
        {
            items.Add(ConvertBillToExportItem(bill, config));
        }

        // 2. Actual Recurring & One-Time Bills
        var recurringProfiles = _db.RecurringExpenses.GetAll()
            .Where(r => !r.IsEstimate);
        foreach (var profile in recurringProfiles)
        {
            foreach (var record in profile.History)
            {
                items.Add(ConvertRecurringRecordToExportItem(profile, record, config));
            }
        }

        // 3. Submitted / Paid Employee Expense Reports
        var expenseReports = _db.ExpenseReports.GetAll()
            .Where(r => r.Status != ExpenseReportStatus.Draft);
        foreach (var report in expenseReports)
        {
            items.Add(ConvertExpenseReportToExportItem(report, config));
        }

        // 4. Payable / Paid / Clawback Commissions
        var commissions = _db.CommissionLedgerRecords.GetAll()
            .Where(c => c.Status != CommissionStatus.Pending);
        foreach (var comm in commissions)
        {
            items.Add(ConvertCommissionToExportItem(comm, config));
        }

        return items.OrderByDescending(i => i.Date).ToList();
    }

    public SagePayableExportItem ConvertBillToExportItem(Bill bill, Sage50ExportConfig? config = null)
    {
        config ??= _db.CompanyProfile?.GetAccounting()?.SageSettings ?? new Sage50ExportConfig();
        var po = !string.IsNullOrEmpty(bill.PurchaseOrderId) ? _db.PurchaseOrders.GetById(bill.PurchaseOrderId) : null;
        var supplier = !string.IsNullOrEmpty(bill.SupplierId) ? _db.Suppliers.GetById(bill.SupplierId) : null;

        var vendorName = !string.IsNullOrWhiteSpace(bill.SupplierName)
            ? bill.SupplierName
            : (!string.IsNullOrWhiteSpace(supplier?.Name) ? supplier.Name : (po?.Supplier?.BusinessName ?? string.Empty));

        supplier ??= FindSupplierByName(vendorName);

        bool isCredit = bill.IsCreditNote || bill.Amount < 0m;
        decimal absTotal = Math.Round(Math.Abs(bill.Amount), 2, MidpointRounding.AwayFromZero);

        decimal absTps = Math.Round(Math.Abs(bill.TpsAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTvq = Math.Round(Math.Abs(bill.TvqAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTax = Math.Round(Math.Abs(bill.TaxAmount), 2, MidpointRounding.AwayFromZero);
        bool hasExplicitTax = bill.IsTaxExempt || absTps > 0m || absTvq > 0m || absTax > 0m || bill.SubTotal > 0m;

        if (bill.IsTaxExempt)
        {
            absTps = 0m;
            absTvq = 0m;
        }
        else if (absTps == 0m && absTvq == 0m)
        {
            if (absTax > 0m)
            {
                (absTps, absTvq) = FinancialCalculationEngine.SplitCombinedTax(absTax);
            }
            else if (bill.SubTotal > 0m && Math.Abs(bill.SubTotal) < absTotal)
            {
                var inferredTax = absTotal - Math.Round(Math.Abs(bill.SubTotal), 2, MidpointRounding.AwayFromZero);
                (absTps, absTvq) = FinancialCalculationEngine.SplitCombinedTax(inferredTax);
            }
        }

        decimal absSubTotal = Math.Max(0m, absTotal - (absTps + absTvq));
        decimal sign = isCredit ? -1m : 1m;

        var mappedVendorGl = !string.IsNullOrWhiteSpace(vendorName) && config.VendorDefaultGlMappings.TryGetValue(vendorName.Trim(), out var vGl) && !string.IsNullOrWhiteSpace(vGl)
            ? vGl.Trim()
            : null;

        var glCode = !string.IsNullOrWhiteSpace(bill.GlAccountCode)
            ? bill.GlAccountCode.Trim()
            : (!string.IsNullOrWhiteSpace(supplier?.ContactInfo?.DefaultGlAccountCode)
                ? supplier.ContactInfo.DefaultGlAccountCode.Trim()
                : (mappedVendorGl ?? config.DefaultExpenseGl));

        var itemCode = !string.IsNullOrWhiteSpace(bill.SageItemCode)
            ? bill.SageItemCode.Trim()
            : (!string.IsNullOrWhiteSpace(supplier?.ContactInfo?.DefaultSageItemCode)
                ? supplier.ContactInfo.DefaultSageItemCode.Trim()
                : config.DefaultPurchaseItemCode);

        var defaultLineCode = ResolveDetailLineCode(glCode, itemCode, isSales: false, config);

        var lines = bill.LineItems.Count > 0
            ? bill.LineItems.Select(li =>
            {
                var lineGl = !string.IsNullOrWhiteSpace(li.GlAccountCode) ? li.GlAccountCode.Trim() : glCode;
                var lineItem = !string.IsNullOrWhiteSpace(li.SageItemCode) ? li.SageItemCode.Trim() : itemCode;
                var resolvedLineCode = ResolveDetailLineCode(lineGl, lineItem, isSales: false, config);
                return (
                    Description: string.IsNullOrWhiteSpace(li.Description) ? bill.Description : li.Description,
                    Quantity: li.Quantity > 0 ? li.Quantity : 1m,
                    RawLineTotal: li.Total * sign,
                    GlAccountOrItemCode: (string?)resolvedLineCode
                );
            }).ToList()
            :
            [
                (
                    Description: string.IsNullOrWhiteSpace(bill.Description) ? "Purchase" : bill.Description,
                    Quantity: 1m,
                    RawLineTotal: absSubTotal * sign,
                    GlAccountOrItemCode: (string?)defaultLineCode
                )
            ];

        var refNumber = !string.IsNullOrWhiteSpace(bill.InvoiceNumber)
            ? bill.InvoiceNumber
            : (!string.IsNullOrWhiteSpace(bill.Description) ? bill.Description : $"BILL-{bill.Date:yyyyMMdd}");

        return new SagePayableExportItem
        {
            Id = bill.Id,
            SourceType = SagePayableSourceType.SupplierBill,
            InvoiceOrReferenceNumber = refNumber,
            Description = bill.Description,
            VendorName = vendorName,
            SupplierId = !string.IsNullOrEmpty(bill.SupplierId) ? bill.SupplierId : supplier?.Id,
            PurchaseOrderId = bill.PurchaseOrderId,
            PurchaseOrderNumber = po?.PoNumber,
            Date = bill.Date,
            DueDate = bill.DueDate,
            SubTotal = absSubTotal * sign,
            TpsAmount = absTps * sign,
            TvqAmount = absTvq * sign,
            TaxAmount = (absTps + absTvq) * sign,
            TotalAmount = absTotal * sign,
            IsTaxExempt = bill.IsTaxExempt,
            IsCreditNote = isCredit,
            HasExplicitTaxBreakdown = hasExplicitTax,
            Status = bill.Status,
            GlAccountCode = glCode,
            SageItemCode = itemCode,
            ExportedToSageAt = bill.ExportedToSageAt,
            SageBatchId = bill.SageBatchId,
            LineItems = lines,
            UnderlyingEntity = bill
        };
    }

    private SagePayableExportItem ConvertRecurringRecordToExportItem(RecurringExpense profile, RecurringBillRecord record, Sage50ExportConfig config)
    {
        bool isTaxExempt = record.IsTaxExempt ?? profile.IsTaxExempt;
        bool isCredit = record.Amount < 0m;
        decimal absTotal = Math.Round(Math.Abs(record.Amount), 2, MidpointRounding.AwayFromZero);
        decimal sign = isCredit ? -1m : 1m;

        decimal absTps = Math.Round(Math.Abs(record.TpsAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTvq = Math.Round(Math.Abs(record.TvqAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTax = Math.Round(Math.Abs(record.TaxAmount), 2, MidpointRounding.AwayFromZero);

        if (isTaxExempt)
        {
            absTps = 0m;
            absTvq = 0m;
        }
        else if (absTps == 0m && absTvq == 0m)
        {
            if (absTax > 0m)
            {
                (absTps, absTvq) = FinancialCalculationEngine.SplitCombinedTax(absTax);
            }
            else if (record.SubTotal > 0m && Math.Abs(record.SubTotal) < absTotal)
            {
                var inferredTax = absTotal - Math.Round(Math.Abs(record.SubTotal), 2, MidpointRounding.AwayFromZero);
                (absTps, absTvq) = FinancialCalculationEngine.SplitCombinedTax(inferredTax);
            }
            else
            {
                var breakdown = FinancialCalculationEngine.CalculateFromGrandTotal(absTotal, isTaxExempt: false);
                absTps = breakdown.TpsAmount;
                absTvq = breakdown.TvqAmount;
            }
        }

        decimal absSubTotal = Math.Max(0m, absTotal - (absTps + absTvq));

        var vendorName = !string.IsNullOrWhiteSpace(profile.Supplier) ? profile.Supplier : profile.Name;
        var supplier = !string.IsNullOrEmpty(profile.SupplierId) ? _db.Suppliers.GetById(profile.SupplierId) : FindSupplierByName(vendorName);
        var mappedVendorGl = !string.IsNullOrWhiteSpace(vendorName) && config.VendorDefaultGlMappings.TryGetValue(vendorName.Trim(), out var vGl) && !string.IsNullOrWhiteSpace(vGl)
            ? vGl.Trim()
            : null;

        var glCode = !string.IsNullOrWhiteSpace(record.GlAccountCode)
            ? record.GlAccountCode.Trim()
            : (!string.IsNullOrWhiteSpace(profile.GlAccountCode)
                ? profile.GlAccountCode.Trim()
                : (!string.IsNullOrWhiteSpace(supplier?.ContactInfo?.DefaultGlAccountCode)
                    ? supplier.ContactInfo.DefaultGlAccountCode.Trim()
                    : (mappedVendorGl ?? (!string.IsNullOrWhiteSpace(config.DefaultRecurringExpenseGl) ? config.DefaultRecurringExpenseGl : config.DefaultExpenseGl))));

        var itemCode = !string.IsNullOrWhiteSpace(record.SageItemCode)
            ? record.SageItemCode.Trim()
            : (!string.IsNullOrWhiteSpace(profile.SageItemCode) ? profile.SageItemCode.Trim() : config.DefaultPurchaseItemCode);

        var effectiveCode = ResolveDetailLineCode(glCode, itemCode, isSales: false, config);
        var refNumber = !string.IsNullOrWhiteSpace(record.InvoiceNumber)
            ? record.InvoiceNumber
            : $"{profile.Name}-{record.Date:yyyyMMdd}";

        return new SagePayableExportItem
        {
            Id = record.Id,
            ParentEntityId = profile.Id,
            SourceType = SagePayableSourceType.RecurringBill,
            IsOneTimeRecurring = profile.Frequency == RecurringExpenseFrequency.OneTime,
            InvoiceOrReferenceNumber = refNumber,
            Description = profile.Name,
            VendorName = vendorName,
            SupplierId = !string.IsNullOrEmpty(profile.SupplierId) ? profile.SupplierId : supplier?.Id,
            Date = record.Date,
            DueDate = record.DueDate ?? record.Date.AddDays(30),
            SubTotal = absSubTotal * sign,
            TpsAmount = absTps * sign,
            TvqAmount = absTvq * sign,
            TaxAmount = (absTps + absTvq) * sign,
            TotalAmount = absTotal * sign,
            IsTaxExempt = isTaxExempt,
            IsCreditNote = isCredit,
            HasExplicitTaxBreakdown = true,
            Status = record.IsPaid || record.BalanceDue <= 0m ? BillStatus.Paid : BillStatus.Unpaid,
            GlAccountCode = glCode,
            SageItemCode = itemCode,
            ExportedToSageAt = record.ExportedToSageAt,
            SageBatchId = record.SageBatchId,
            LineItems =
            [
                (
                    Description: profile.Name,
                    Quantity: 1m,
                    RawLineTotal: absSubTotal * sign,
                    GlAccountOrItemCode: effectiveCode
                )
            ],
            UnderlyingEntity = profile,
            SecondaryEntity = record
        };
    }

    private SagePayableExportItem ConvertExpenseReportToExportItem(ExpenseReport report, Sage50ExportConfig config)
    {
        var reportDate = report.DateSubmitted ?? report.DateCreated;
        var mappedVendorGl = !string.IsNullOrWhiteSpace(report.EmployeeName) && config.VendorDefaultGlMappings.TryGetValue(report.EmployeeName.Trim(), out var vGl) && !string.IsNullOrWhiteSpace(vGl)
            ? vGl.Trim()
            : null;

        var defaultExpenseGl = mappedVendorGl ?? (!string.IsNullOrWhiteSpace(config.DefaultEmployeeExpenseGl) ? config.DefaultEmployeeExpenseGl : config.DefaultExpenseGl);
        var defaultKmGl = !string.IsNullOrWhiteSpace(config.DefaultKilometrageGl) ? config.DefaultKilometrageGl : defaultExpenseGl;

        var lines = new List<(string Description, decimal Quantity, decimal RawLineTotal, string? GlAccountOrItemCode)>();
        foreach (var item in report.LineItems)
        {
            var lineGl = !string.IsNullOrWhiteSpace(item.GlAccountCode)
                ? item.GlAccountCode.Trim()
                : (item.IsKilometrage ? defaultKmGl : defaultExpenseGl);
            var code = ResolveDetailLineCode(lineGl, item.SageItemCode, isSales: false, config);

            lines.Add((
                Description: string.IsNullOrWhiteSpace(item.Description) ? (item.IsKilometrage ? "Kilometrage" : "Expense") : item.Description,
                Quantity: 1m,
                RawLineTotal: item.EffectiveSubTotal,
                GlAccountOrItemCode: code
            ));
        }

        var firstCustomGl = report.LineItems.Select(i => i.GlAccountCode).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        var reportGl = !string.IsNullOrWhiteSpace(firstCustomGl) ? firstCustomGl.Trim() : defaultExpenseGl;

        var refNumber = !string.IsNullOrWhiteSpace(report.ReportNumber)
            ? report.ReportNumber
            : $"EXP-{reportDate:yyyyMMdd}";

        return new SagePayableExportItem
        {
            Id = report.Id,
            SourceType = SagePayableSourceType.ExpenseReport,
            InvoiceOrReferenceNumber = refNumber,
            Description = $"Expense Report {refNumber}",
            VendorName = report.EmployeeName,
            Date = reportDate,
            DueDate = reportDate.AddDays(30),
            SubTotal = report.SubTotal,
            TpsAmount = report.TpsAmount,
            TvqAmount = report.TvqAmount,
            TaxAmount = report.TaxAmount,
            TotalAmount = report.TotalAmount,
            IsTaxExempt = report.LineItems.Count > 0 && report.LineItems.All(i => i.IsTaxExempt),
            IsCreditNote = report.TotalAmount < 0m,
            HasExplicitTaxBreakdown = true,
            Status = report.Status,
            GlAccountCode = reportGl,
            SageItemCode = config.DefaultPurchaseItemCode,
            ExportedToSageAt = report.ExportedToSageAt,
            SageBatchId = report.SageBatchId,
            LineItems = lines,
            UnderlyingEntity = report
        };
    }

    private SagePayableExportItem ConvertCommissionToExportItem(CommissionLedgerRecord comm, Sage50ExportConfig config)
    {
        var proj = !string.IsNullOrEmpty(comm.ProjectId) ? _db.Projects.GetById(comm.ProjectId) : null;
        bool isCredit = comm.Status == CommissionStatus.Clawback || comm.CommissionAmount < 0m;
        decimal absBase = Math.Round(Math.Abs(comm.CommissionAmount), 2, MidpointRounding.AwayFromZero);
        decimal sign = isCredit ? -1m : 1m;

        decimal absTps = 0m;
        decimal absTvq = 0m;
        if (comm.ApplyTaxes)
        {
            if (comm.TpsAmount != 0m || comm.TvqAmount != 0m)
            {
                absTps = Math.Round(Math.Abs(comm.TpsAmount), 2, MidpointRounding.AwayFromZero);
                absTvq = Math.Round(Math.Abs(comm.TvqAmount), 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                var breakdown = FinancialCalculationEngine.CalculateFromSubTotal(absBase);
                absTps = breakdown.TpsAmount;
                absTvq = breakdown.TvqAmount;
            }
        }

        decimal absTotal = absBase + absTps + absTvq;
        var mappedVendorGl = !string.IsNullOrWhiteSpace(comm.BeneficiaryName) && config.VendorDefaultGlMappings.TryGetValue(comm.BeneficiaryName.Trim(), out var vGl) && !string.IsNullOrWhiteSpace(vGl)
            ? vGl.Trim()
            : null;

        var glCode = !string.IsNullOrWhiteSpace(comm.GlAccountCode)
            ? comm.GlAccountCode.Trim()
            : (mappedVendorGl ?? (!string.IsNullOrWhiteSpace(config.DefaultCommissionGl) ? config.DefaultCommissionGl : config.DefaultExpenseGl));
        var effectiveCode = ResolveDetailLineCode(glCode, comm.SageItemCode, isSales: false, config);
        var projCode = proj?.ProjectNumber ?? comm.Id[..Math.Min(6, comm.Id.Length)];
        var refNumber = $"COMM-{projCode}-{comm.DateGenerated:yyyyMMdd}";

        return new SagePayableExportItem
        {
            Id = comm.Id,
            SourceType = SagePayableSourceType.Commission,
            InvoiceOrReferenceNumber = refNumber,
            Description = $"Commission ({proj?.DisplayName ?? projCode})",
            VendorName = comm.BeneficiaryName,
            Date = comm.DateGenerated.ToLocalTime().Date,
            DueDate = comm.DateGenerated.ToLocalTime().Date.AddDays(30),
            SubTotal = absBase * sign,
            TpsAmount = absTps * sign,
            TvqAmount = absTvq * sign,
            TaxAmount = (absTps + absTvq) * sign,
            TotalAmount = absTotal * sign,
            IsTaxExempt = !comm.ApplyTaxes,
            IsCreditNote = isCredit,
            HasExplicitTaxBreakdown = true,
            Status = comm.Status,
            GlAccountCode = glCode,
            SageItemCode = comm.SageItemCode,
            ExportedToSageAt = comm.ExportedToSageAt,
            SageBatchId = comm.SageBatchId,
            LineItems =
            [
                (
                    Description: $"Commission - {proj?.DisplayName ?? projCode}",
                    Quantity: 1m,
                    RawLineTotal: absBase * sign,
                    GlAccountOrItemCode: effectiveCode
                )
            ],
            UnderlyingEntity = comm
        };
    }

    // =========================================================================
    // 3. PRE-FLIGHT VALIDATION (Including Chart of Accounts Verification)
    // =========================================================================

    public SageExportValidationResult ValidateForExport(
        IEnumerable<Invoice> invoices,
        IEnumerable<SagePayableExportItem> payables,
        Sage50ExportConfig config)
    {
        var result = new SageExportValidationResult();

        if (string.IsNullOrWhiteSpace(config.VersionHeader))
        {
            result.Errors.Add("Sage version header is required (e.g. 12001).");
        }
        if (string.IsNullOrWhiteSpace(config.AccountsReceivableGl))
        {
            result.Errors.Add("Accounts Receivable GL account is required.");
        }
        if (string.IsNullOrWhiteSpace(config.AccountsPayableGl))
        {
            result.Errors.Add("Accounts Payable GL account is required.");
        }
        if (string.IsNullOrWhiteSpace(config.DefaultSalesRevenueGl))
        {
            result.Errors.Add("Default Sales Revenue GL account is required.");
        }
        if (string.IsNullOrWhiteSpace(config.DefaultExpenseGl))
        {
            result.Errors.Add("Default Expense GL account is required.");
        }
        if (string.IsNullOrWhiteSpace(config.TpsTaxCode) || string.IsNullOrWhiteSpace(config.TvqTaxCode))
        {
            result.Errors.Add("Both TPS and TVQ tax codes are required for Sage 50 Canadian Edition.");
        }

        bool hasAccountBaseline = config.ImportedAccounts.Count > 0;
        HashSet<string>? validAccountNumbers = hasAccountBaseline
            ? new HashSet<string>(config.ImportedAccounts.Select(a => a.AccountNumber.Trim()), StringComparer.OrdinalIgnoreCase)
            : null;

        if (validAccountNumbers != null)
        {
            if (!string.IsNullOrWhiteSpace(config.AccountsReceivableGl) && !validAccountNumbers.Contains(config.AccountsReceivableGl.Trim()))
            {
                result.Errors.Add($"Accounts Receivable GL '{config.AccountsReceivableGl}' does not exist in the imported Sage 50 Chart of Accounts.");
            }
            if (!string.IsNullOrWhiteSpace(config.AccountsPayableGl) && !validAccountNumbers.Contains(config.AccountsPayableGl.Trim()))
            {
                result.Errors.Add($"Accounts Payable GL '{config.AccountsPayableGl}' does not exist in the imported Sage 50 Chart of Accounts.");
            }
        }

        bool hasCustomerBaseline = config.ImportedCustomers.Count > 0;
        bool hasVendorBaseline = config.ImportedVendors.Count > 0;

        foreach (var inv in invoices ?? [])
        {
            var client = ResolveClientForInvoice(inv);
            if (string.IsNullOrWhiteSpace(client?.BusinessName))
            {
                result.Errors.Add($"Invoice {inv.InvoiceNumber} is missing a client business name.");
            }
            else if (hasCustomerBaseline)
            {
                var resolvedName = ResolveSageCustomerName(client, config);
                bool matchesBaseline = config.ImportedCustomers.Any(c =>
                    string.Equals(c.Name, resolvedName, StringComparison.OrdinalIgnoreCase));
                if (!matchesBaseline && !config.IncludeNewCustomersAndVendorsInImp)
                {
                    result.Warnings.Add($"Client '{client.BusinessName}' (Invoice {inv.InvoiceNumber}) is not mapped to an imported Sage 50 Customer.");
                }
            }

            var invGl = ResolveInvoiceGlAccount(inv, config);
            if (validAccountNumbers != null && !string.IsNullOrWhiteSpace(invGl) && !validAccountNumbers.Contains(invGl.Trim()))
            {
                result.Errors.Add($"Invoice {inv.InvoiceNumber} uses GL account '{invGl}', which does not exist in the imported Sage 50 Chart of Accounts.");
            }

            if (inv.GrandTotal <= 0m)
            {
                result.Warnings.Add($"Invoice {inv.InvoiceNumber} has an amount of {inv.GrandTotal:C2}.");
            }
        }

        foreach (var payable in payables ?? [])
        {
            if (string.IsNullOrWhiteSpace(payable.VendorName))
            {
                result.Errors.Add($"{payable.TypeBadge} '{payable.InvoiceOrReferenceNumber}' (Date: {payable.Date:yyyy-MM-dd}) is missing a supplier/vendor name.");
            }
            else if (hasVendorBaseline)
            {
                var supplier = !string.IsNullOrEmpty(payable.SupplierId) ? _db.Suppliers.GetById(payable.SupplierId) : FindSupplierByName(payable.VendorName);
                var resolvedVendor = ResolveSageVendorName(payable.VendorName, supplier?.ContactInfo, config);
                bool matchesBaseline = config.ImportedVendors.Any(v =>
                    string.Equals(v.Name, resolvedVendor, StringComparison.OrdinalIgnoreCase));
                if (!matchesBaseline && !config.IncludeNewCustomersAndVendorsInImp)
                {
                    result.Warnings.Add($"Vendor '{payable.VendorName}' ({payable.TypeBadge} {payable.InvoiceOrReferenceNumber}) is not mapped to an imported Sage 50 Vendor.");
                }
            }

            var payGl = ResolvePayableGlAccount(payable, config);
            if (validAccountNumbers != null && !string.IsNullOrWhiteSpace(payGl) && !validAccountNumbers.Contains(payGl.Trim()))
            {
                result.Errors.Add($"{payable.TypeBadge} '{payable.InvoiceOrReferenceNumber}' ({payable.VendorName}) uses GL account '{payGl}', which does not exist in the imported Sage 50 Chart of Accounts.");
            }

            if (payable.TotalAmount == 0m || (!payable.IsCreditNote && payable.TotalAmount < 0m))
            {
                result.Warnings.Add($"{payable.TypeBadge} '{payable.InvoiceOrReferenceNumber}' has an amount of {payable.TotalAmount:C2}.");
            }
            else if (!payable.IsTaxExempt && !payable.HasExplicitTaxBreakdown && payable.TaxAmount == 0m)
            {
                result.Warnings.Add($"{payable.TypeBadge} '{payable.InvoiceOrReferenceNumber}' ({payable.VendorName}) has $0.00 TPS/TVQ recorded. Click the row to verify tax breakdown or mark it Tax Exempt.");
            }
        }

        return result;
    }

    // =========================================================================
    // 4. SAGE 50 CANADIAN EDITION .IMP GENERATION
    // =========================================================================

    public byte[] GenerateSage50ImpBytes(
        IEnumerable<Invoice> invoices,
        IEnumerable<SagePayableExportItem> payables,
        Sage50ExportConfig config)
    {
        var impContent = GenerateSage50Imp(invoices, payables, config);
        // Sage 50 Canadian Edition desktop parser reads .imp files in Windows-1252 / Latin-1 (ANSI) encoding.
        return Encoding.Latin1.GetBytes(impContent);
    }

    public string GenerateSage50Imp(
        IEnumerable<Invoice> invoices,
        IEnumerable<SagePayableExportItem> payables,
        Sage50ExportConfig config)
    {
        var invoiceList = (invoices ?? []).ToList();
        var payableList = (payables ?? []).ToList();
        var sb = new StringBuilder();

        var versionHeader = !string.IsNullOrWhiteSpace(config.VersionHeader) ? config.VersionHeader : "12001";
        var countryCode = !string.IsNullOrWhiteSpace(config.CountryCode) ? config.CountryCode : "1";
        var tpsCode = !string.IsNullOrWhiteSpace(config.TpsTaxCode) ? config.TpsTaxCode : "TPS";
        var tvqCode = !string.IsNullOrWhiteSpace(config.TvqTaxCode) ? config.TvqTaxCode : "TVQ";

        // 1. Multi-line Sage 50 <Version> Header (strictly required by Sage 50 line-oriented parser)
        AppendImpLine(sb, "<Version>");
        AppendImpLine(sb, $"\"{EscapeImp(versionHeader)}\", \"{EscapeImp(countryCode)}\"");
        AppendImpLine(sb, "</Version>");

        // 2. Optional <customers> and <vendors> master blocks when new entities exist
        if (config.IncludeNewCustomersAndVendorsInImp)
        {
            EmitNewCustomersBlock(sb, invoiceList, config);
            EmitNewVendorsBlock(sb, payableList, config);
        }

        // 3. Sales Invoices: <SalInvoice> ... </SalInvoice>
        foreach (var inv in invoiceList)
        {
            var client = ResolveClientForInvoice(inv) ?? new ClientInfo();
            var customerName = ResolveSageCustomerName(client, config);
            var street = client.GetFormattedStreetAddress();
            var city = client.BusinessAddressCity ?? string.Empty;
            var state = !string.IsNullOrWhiteSpace(client.BusinessAddressState) ? client.BusinessAddressState : "QC";
            var zip = client.BusinessAddressZip ?? string.Empty;
            var country = !string.IsNullOrWhiteSpace(client.BusinessAddressCountry) ? client.BusinessAddressCountry : "Canada";

            AppendImpLine(sb, "<SalInvoice>");
            // Line 1: Customer header (Name max 35 chars, "0", Street max 35, City max 35, Prov max 2, Postal max 10, Country)
            AppendImpLine(sb, $"\"{EscapeImp(customerName, 35)}\", \"0\", \"{EscapeImp(street, 35)}\", \"{EscapeImp(city, 35)}\", \"{EscapeImp(state, 2)}\", \"{EscapeImp(zip, 10)}\", \"{EscapeImp(country, 20)}\"");

            // Calculate exact Quebec taxes via FinancialCalculationEngine so SubTotal + TPS + TVQ == Header Total
            var taxBreakdown = FinancialCalculationEngine.CalculateFromSubTotal(inv.SubTotal, inv.TaxRate);
            var invoiceGlCode = ResolveInvoiceGlAccount(inv, config);
            var invoiceItemCode = !string.IsNullOrWhiteSpace(inv.SageItemCode)
                ? inv.SageItemCode
                : (!string.IsNullOrWhiteSpace(client.DefaultSageItemCode) ? client.DefaultSageItemCode : config.DefaultSalesItemCode);
            var defaultSalesCode = ResolveDetailLineCode(invoiceGlCode, invoiceItemCode, isSales: true, config);

            var rawLines = inv.LineItems.Count > 0
                ? inv.LineItems.Select(l =>
                {
                    var lineGl = !string.IsNullOrWhiteSpace(l.GlAccountCode) ? l.GlAccountCode.Trim() : invoiceGlCode;
                    var lineItem = !string.IsNullOrWhiteSpace(l.SageItemCode) ? l.SageItemCode.Trim() : invoiceItemCode;
                    var resolvedLineCode = ResolveDetailLineCode(lineGl, lineItem, isSales: true, config);
                    return (
                        Description: string.IsNullOrWhiteSpace(l.Description) ? "Services" : l.Description,
                        Quantity: l.Quantity > 0m ? l.Quantity : 1m,
                        RawLineTotal: l.Total,
                        GlAccountOrItemCode: (string?)resolvedLineCode
                    );
                }).ToList()
                :
                [
                    (
                        Description: "Services",
                        Quantity: 1m,
                        RawLineTotal: taxBreakdown.SubTotal,
                        GlAccountOrItemCode: (string?)defaultSalesCode
                    )
                ];

            var reconciledLines = FinancialCalculationEngine.ProrateLinesWithTaxReconciliation(
                rawLines,
                taxBreakdown.SubTotal,
                taxBreakdown.TpsAmount,
                taxBreakdown.TvqAmount,
                defaultSalesCode);

            // Line 2: Sales Invoice Transaction Options
            // Official layout: "<no. of details>", "<order/quote no.>", "<InvoiceNo>", "<entry date MM-dd-yyyy>", "<paid by (0=Pay Later/AR)>", "<payment ref>", "<total amount>", "<freight amount>"
            var invNum = !string.IsNullOrWhiteSpace(inv.InvoiceNumber) ? inv.InvoiceNumber : "INV";
            var dateStr = inv.Date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            var totalStr = FormatDecimal(taxBreakdown.GrandTotal);
            AppendImpLine(sb, $"\"{reconciledLines.Count}\", \"\", \"{EscapeImp(invNum, 20)}\", \"{dateStr}\", \"0\", \"\", \"{totalStr}\", \"0.00\"");

            // Line 3+: Detail lines with 5-field tax tuples per tax authority
            string exemptFlag = taxBreakdown.IsTaxExempt ? "1" : "0";
            string tpsRateStr = taxBreakdown.IsTaxExempt ? "0.00" : "5.00";
            string tvqRateStr = taxBreakdown.IsTaxExempt ? "0.00" : "9.975";

            foreach (var line in reconciledLines)
            {
                var lineCode = !string.IsNullOrWhiteSpace(line.GlAccountOrItemCode) ? line.GlAccountOrItemCode : defaultSalesCode;
                AppendImpLine(sb,
                    $"\"{EscapeImp(lineCode, 20)}\", \"{FormatDecimal(line.Quantity)}\", \"{FormatDecimal(line.UnitPrice)}\", \"{FormatDecimal(line.LineSubTotal)}\", " +
                    $"\"{EscapeImp(tpsCode)}\", \"{exemptFlag}\", \"1\", \"{tpsRateStr}\", \"{FormatDecimal(line.LineTps)}\", " +
                    $"\"{EscapeImp(tvqCode)}\", \"{exemptFlag}\", \"1\", \"{tvqRateStr}\", \"{FormatDecimal(line.LineTvq)}\"");
            }

            AppendImpLine(sb, "</SalInvoice>");
        }

        // 4. Purchase Invoices: <PurInvoice> ... </PurInvoice>
        foreach (var payable in payableList)
        {
            var supplier = !string.IsNullOrEmpty(payable.SupplierId)
                ? _db.Suppliers.GetById(payable.SupplierId)
                : FindSupplierByName(payable.VendorName);
            var contact = supplier?.ContactInfo;

            var vendorName = ResolveSageVendorName(payable.VendorName, contact, config);
            var street = contact?.GetFormattedStreetAddress() ?? string.Empty;
            var city = contact?.BusinessAddressCity ?? string.Empty;
            var state = !string.IsNullOrWhiteSpace(contact?.BusinessAddressState) ? contact!.BusinessAddressState : "QC";
            var zip = contact?.BusinessAddressZip ?? string.Empty;
            var country = !string.IsNullOrWhiteSpace(contact?.BusinessAddressCountry) ? contact!.BusinessAddressCountry : "Canada";

            AppendImpLine(sb, "<PurInvoice>");
            // Line 1: Vendor header (Vendor max 35 chars, "0", Street max 35, City max 35, Prov max 2, Postal max 10, Country)
            AppendImpLine(sb, $"\"{EscapeImp(vendorName, 35)}\", \"0\", \"{EscapeImp(street, 35)}\", \"{EscapeImp(city, 35)}\", \"{EscapeImp(state, 2)}\", \"{EscapeImp(zip, 10)}\", \"{EscapeImp(country, 20)}\"");

            var payableGl = ResolvePayableGlAccount(payable, config);
            var defaultPurchaseCode = ResolveDetailLineCode(payableGl, payable.SageItemCode, isSales: false, config);

            var reconciledLines = FinancialCalculationEngine.ProrateLinesWithTaxReconciliation(
                payable.LineItems,
                payable.SubTotal,
                payable.TpsAmount,
                payable.TvqAmount,
                defaultPurchaseCode);

            // Line 2: Purchase Invoice Transaction Options
            // Official layout: "<no. of details>", "<quote/order no.>", "<InvoiceNo>", "<entry date MM-dd-yyyy>", "<total amount>", "<discount rate>", "<discount days>", "<net days>", "<freight amt>"
            var billNum = !string.IsNullOrWhiteSpace(payable.InvoiceOrReferenceNumber)
                ? payable.InvoiceOrReferenceNumber
                : (!string.IsNullOrWhiteSpace(payable.Description) ? payable.Description : "BILL");
            var poRef = payable.PurchaseOrderNumber ?? string.Empty;
            var dateStr = payable.Date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            var totalStr = FormatDecimal(payable.TotalAmount);
            int netDays = Math.Max(0, (int)Math.Round((payable.DueDate.Date - payable.Date.Date).TotalDays));
            if (netDays == 0) netDays = 30;

            AppendImpLine(sb, $"\"{reconciledLines.Count}\", \"{EscapeImp(poRef, 20)}\", \"{EscapeImp(billNum, 20)}\", \"{dateStr}\", \"{totalStr}\", \"0.00\", \"0\", \"{netDays}\", \"0.00\"");

            // Line 3+: Detail lines with 5-field tax tuples per tax authority
            bool isExemptOrZeroTax = payable.IsTaxExempt || (payable.TpsAmount == 0m && payable.TvqAmount == 0m);
            string exemptFlag = isExemptOrZeroTax ? "1" : "0";
            string tpsRateStr = isExemptOrZeroTax ? "0.00" : "5.00";
            string tvqRateStr = isExemptOrZeroTax ? "0.00" : "9.975";

            foreach (var line in reconciledLines)
            {
                var lineCode = !string.IsNullOrWhiteSpace(line.GlAccountOrItemCode)
                    ? ResolveDetailLineCode(line.GlAccountOrItemCode, null, isSales: false, config)
                    : defaultPurchaseCode;
                AppendImpLine(sb,
                    $"\"{EscapeImp(lineCode, 20)}\", \"{FormatDecimal(line.Quantity)}\", \"{FormatDecimal(line.UnitPrice)}\", \"{FormatDecimal(line.LineSubTotal)}\", " +
                    $"\"{EscapeImp(tpsCode)}\", \"{exemptFlag}\", \"1\", \"{tpsRateStr}\", \"{FormatDecimal(line.LineTps)}\", " +
                    $"\"{EscapeImp(tvqCode)}\", \"{exemptFlag}\", \"1\", \"{tvqRateStr}\", \"{FormatDecimal(line.LineTvq)}\"");
            }

            AppendImpLine(sb, "</PurInvoice>");
        }

        return sb.ToString();
    }

    private static string ResolveDetailLineCode(string? glAccountCode, string? explicitItemCode, bool isSales, Sage50ExportConfig config)
    {
        if (!string.IsNullOrWhiteSpace(explicitItemCode))
        {
            return explicitItemCode.Trim();
        }

        var cleanGl = glAccountCode?.Trim() ?? string.Empty;

        if (config.PreferServiceItemCodesFromGl && config.ImportedServiceItems.Count > 0 && !string.IsNullOrEmpty(cleanGl))
        {
            var matchedItem = config.ImportedServiceItems.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.ItemId) &&
                string.Equals(isSales ? item.LinkedRevenueGl?.Trim() : item.LinkedExpenseGl?.Trim(), cleanGl, StringComparison.OrdinalIgnoreCase));

            if (matchedItem != null)
            {
                return matchedItem.ItemId.Trim();
            }
        }

        var defaultItem = isSales ? config.DefaultSalesItemCode : config.DefaultPurchaseItemCode;
        if (!string.IsNullOrWhiteSpace(defaultItem))
        {
            return defaultItem.Trim();
        }

        if (!string.IsNullOrEmpty(cleanGl))
        {
            return cleanGl;
        }

        return isSales ? config.DefaultSalesRevenueGl : config.DefaultExpenseGl;
    }

    private void EmitNewCustomersBlock(StringBuilder sb, List<Invoice> invoices, Sage50ExportConfig config)
    {
        var seen = new HashSet<string>(config.ImportedCustomers.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var newLines = new List<string>();

        foreach (var inv in invoices)
        {
            var client = ResolveClientForInvoice(inv);
            if (client == null || string.IsNullOrWhiteSpace(client.BusinessName)) continue;

            var resolvedName = ResolveSageCustomerName(client, config);
            if (seen.Add(resolvedName))
            {
                var street = client.GetFormattedStreetAddress();
                var state = !string.IsNullOrWhiteSpace(client.BusinessAddressState) ? client.BusinessAddressState : "QC";
                var country = !string.IsNullOrWhiteSpace(client.BusinessAddressCountry) ? client.BusinessAddressCountry : "Canada";
                newLines.Add(
                    $"\"{EscapeImp(resolvedName, 35)}\", \"0\", \"{EscapeImp(client.ContactPersonName, 35)}\", " +
                    $"\"{EscapeImp(street, 35)}\", \"\", \"{EscapeImp(client.BusinessAddressCity, 35)}\", " +
                    $"\"{EscapeImp(state, 2)}\", \"{EscapeImp(client.BusinessAddressZip, 10)}\", \"{EscapeImp(country, 20)}\", " +
                    $"\"{EscapeImp(client.ContactPersonPhone, 20)}\", \"\", \"\", \"{EscapeImp(client.ContactPersonEmail, 50)}\"");
            }
        }

        if (newLines.Count > 0)
        {
            AppendImpLine(sb, "<customers>");
            foreach (var line in newLines) AppendImpLine(sb, line);
            AppendImpLine(sb, "</customers>");
        }
    }

    private void EmitNewVendorsBlock(StringBuilder sb, List<SagePayableExportItem> payables, Sage50ExportConfig config)
    {
        var seen = new HashSet<string>(config.ImportedVendors.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        var newLines = new List<string>();

        foreach (var payable in payables)
        {
            if (string.IsNullOrWhiteSpace(payable.VendorName)) continue;
            var supplier = !string.IsNullOrEmpty(payable.SupplierId)
                ? _db.Suppliers.GetById(payable.SupplierId)
                : FindSupplierByName(payable.VendorName);
            var contact = supplier?.ContactInfo;

            var resolvedName = ResolveSageVendorName(payable.VendorName, contact, config);
            if (seen.Add(resolvedName))
            {
                var street = contact?.GetFormattedStreetAddress() ?? string.Empty;
                var state = !string.IsNullOrWhiteSpace(contact?.BusinessAddressState) ? contact!.BusinessAddressState : "QC";
                var country = !string.IsNullOrWhiteSpace(contact?.BusinessAddressCountry) ? contact!.BusinessAddressCountry : "Canada";
                newLines.Add(
                    $"\"{EscapeImp(resolvedName, 35)}\", \"0\", \"{EscapeImp(contact?.ContactPersonName, 35)}\", " +
                    $"\"{EscapeImp(street, 35)}\", \"\", \"{EscapeImp(contact?.BusinessAddressCity, 35)}\", " +
                    $"\"{EscapeImp(state, 2)}\", \"{EscapeImp(contact?.BusinessAddressZip, 10)}\", \"{EscapeImp(country, 20)}\", " +
                    $"\"{EscapeImp(contact?.ContactPersonPhone, 20)}\", \"\", \"\", \"{EscapeImp(contact?.ContactPersonEmail, 50)}\"");
            }
        }

        if (newLines.Count > 0)
        {
            AppendImpLine(sb, "<vendors>");
            foreach (var line in newLines) AppendImpLine(sb, line);
            AppendImpLine(sb, "</vendors>");
        }
    }

    // =========================================================================
    // 5. CSV GENERATION
    // =========================================================================

    public string GenerateSalesInvoicesCsv(IEnumerable<Invoice> invoices, Sage50ExportConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("InvoiceNumber,Date,DueDate,ClientName,SageCustomerName,Street,City,Province,PostalCode,SubTotal,TPS,TVQ,TaxAmount,GrandTotal,GlAccount,Status");

        foreach (var inv in invoices ?? [])
        {
            var client = ResolveClientForInvoice(inv) ?? new ClientInfo();
            var sageName = ResolveSageCustomerName(client, config);
            var taxBreakdown = FinancialCalculationEngine.CalculateFromSubTotal(inv.SubTotal, inv.TaxRate);
            var glAccount = ResolveInvoiceGlAccount(inv, config);

            var row = new[]
            {
                CsvSanitizer.EscapeCsv(inv.InvoiceNumber),
                inv.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                inv.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(client.BusinessName),
                CsvSanitizer.EscapeCsv(sageName),
                CsvSanitizer.EscapeCsv(client.GetFormattedStreetAddress()),
                CsvSanitizer.EscapeCsv(client.BusinessAddressCity),
                CsvSanitizer.EscapeCsv(client.BusinessAddressState),
                CsvSanitizer.EscapeCsv(client.BusinessAddressZip),
                FormatDecimal(taxBreakdown.SubTotal),
                FormatDecimal(taxBreakdown.TpsAmount),
                FormatDecimal(taxBreakdown.TvqAmount),
                FormatDecimal(taxBreakdown.TaxAmount),
                FormatDecimal(taxBreakdown.GrandTotal),
                CsvSanitizer.EscapeCsv(glAccount),
                CsvSanitizer.EscapeCsv(inv.Status)
            };
            sb.AppendLine(string.Join(",", row));
        }

        return sb.ToString();
    }

    public string GeneratePurchaseInvoicesCsv(IEnumerable<SagePayableExportItem> payables, Sage50ExportConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Type,BillNumber,InvoiceNumber,Date,DueDate,SupplierName,SageVendorName,PurchaseOrderId,SubTotal,TPS,TVQ,TaxAmount,TotalAmount,GlAccount,Status");

        foreach (var payable in payables ?? [])
        {
            var supplier = !string.IsNullOrEmpty(payable.SupplierId)
                ? _db.Suppliers.GetById(payable.SupplierId)
                : FindSupplierByName(payable.VendorName);
            var sageVendor = ResolveSageVendorName(payable.VendorName, supplier?.ContactInfo, config);
            var glAccount = ResolvePayableGlAccount(payable, config);

            var row = new[]
            {
                CsvSanitizer.EscapeCsv(payable.TypeBadge),
                CsvSanitizer.EscapeCsv(payable.Description),
                CsvSanitizer.EscapeCsv(payable.InvoiceOrReferenceNumber),
                payable.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                payable.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(payable.VendorName),
                CsvSanitizer.EscapeCsv(sageVendor),
                CsvSanitizer.EscapeCsv(payable.PurchaseOrderNumber ?? payable.PurchaseOrderId ?? ""),
                FormatDecimal(payable.SubTotal),
                FormatDecimal(payable.TpsAmount),
                FormatDecimal(payable.TvqAmount),
                FormatDecimal(payable.TaxAmount),
                FormatDecimal(payable.TotalAmount),
                CsvSanitizer.EscapeCsv(glAccount),
                CsvSanitizer.EscapeCsv(payable.Status)
            };
            sb.AppendLine(string.Join(",", row));
        }

        return sb.ToString();
    }

    // =========================================================================
    // 6. EXPORT STATUS TRACKING (PERSISTENCE ACROSS ALL 5 REPOSITORIES)
    // =========================================================================

    public void MarkAsExported(
        IEnumerable<Invoice> invoices,
        IEnumerable<SagePayableExportItem> payables,
        string batchId,
        bool overwriteExistingBatch = false)
    {
        var now = DateTime.UtcNow;

        foreach (var inv in invoices ?? [])
        {
            if (!overwriteExistingBatch && inv.ExportedToSageAt.HasValue) continue;
            inv.ExportedToSageAt = now;
            inv.SageBatchId = batchId;
            _db.Invoices.Save(inv);
        }

        var modifiedRecurringIds = new HashSet<string>();

        foreach (var payable in payables ?? [])
        {
            if (!overwriteExistingBatch && payable.ExportedToSageAt.HasValue) continue;
            payable.ExportedToSageAt = now;
            payable.SageBatchId = batchId;

            switch (payable.SourceType)
            {
                case SagePayableSourceType.SupplierBill:
                    var bill = payable.UnderlyingEntity as Bill ?? _db.Bills.GetById(payable.Id);
                    if (bill != null)
                    {
                        bill.ExportedToSageAt = now;
                        bill.SageBatchId = batchId;
                        _db.Bills.Save(bill);
                    }
                    break;

                case SagePayableSourceType.RecurringBill:
                    var profile = payable.UnderlyingEntity as RecurringExpense
                        ?? (!string.IsNullOrEmpty(payable.ParentEntityId) ? _db.RecurringExpenses.GetById(payable.ParentEntityId) : null);
                    var record = payable.SecondaryEntity as RecurringBillRecord
                        ?? profile?.History.FirstOrDefault(h => h.Id == payable.Id);
                    if (profile != null && record != null)
                    {
                        record.ExportedToSageAt = now;
                        record.SageBatchId = batchId;
                        if (modifiedRecurringIds.Add(profile.Id))
                        {
                            _db.RecurringExpenses.Save(profile);
                        }
                    }
                    break;

                case SagePayableSourceType.ExpenseReport:
                    var report = payable.UnderlyingEntity as ExpenseReport ?? _db.ExpenseReports.GetById(payable.Id);
                    if (report != null)
                    {
                        report.ExportedToSageAt = now;
                        report.SageBatchId = batchId;
                        _db.ExpenseReports.Save(report);
                    }
                    break;

                case SagePayableSourceType.Commission:
                    var comm = payable.UnderlyingEntity as CommissionLedgerRecord ?? _db.CommissionLedgerRecords.GetById(payable.Id);
                    if (comm != null)
                    {
                        comm.ExportedToSageAt = now;
                        comm.SageBatchId = batchId;
                        _db.CommissionLedgerRecords.Save(comm);
                    }
                    break;
            }
        }
    }

    public void UnmarkExported(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables)
    {
        foreach (var inv in invoices ?? [])
        {
            inv.ExportedToSageAt = null;
            inv.SageBatchId = null;
            _db.Invoices.Save(inv);
        }

        var modifiedRecurringIds = new HashSet<string>();

        foreach (var payable in payables ?? [])
        {
            payable.ExportedToSageAt = null;
            payable.SageBatchId = null;

            switch (payable.SourceType)
            {
                case SagePayableSourceType.SupplierBill:
                    var bill = payable.UnderlyingEntity as Bill ?? _db.Bills.GetById(payable.Id);
                    if (bill != null)
                    {
                        bill.ExportedToSageAt = null;
                        bill.SageBatchId = null;
                        _db.Bills.Save(bill);
                    }
                    break;

                case SagePayableSourceType.RecurringBill:
                    var profile = payable.UnderlyingEntity as RecurringExpense
                        ?? (!string.IsNullOrEmpty(payable.ParentEntityId) ? _db.RecurringExpenses.GetById(payable.ParentEntityId) : null);
                    var record = payable.SecondaryEntity as RecurringBillRecord
                        ?? profile?.History.FirstOrDefault(h => h.Id == payable.Id);
                    if (profile != null && record != null)
                    {
                        record.ExportedToSageAt = null;
                        record.SageBatchId = null;
                        if (modifiedRecurringIds.Add(profile.Id))
                        {
                            _db.RecurringExpenses.Save(profile);
                        }
                    }
                    break;

                case SagePayableSourceType.ExpenseReport:
                    var report = payable.UnderlyingEntity as ExpenseReport ?? _db.ExpenseReports.GetById(payable.Id);
                    if (report != null)
                    {
                        report.ExportedToSageAt = null;
                        report.SageBatchId = null;
                        _db.ExpenseReports.Save(report);
                    }
                    break;

                case SagePayableSourceType.Commission:
                    var comm = payable.UnderlyingEntity as CommissionLedgerRecord ?? _db.CommissionLedgerRecords.GetById(payable.Id);
                    if (comm != null)
                    {
                        comm.ExportedToSageAt = null;
                        comm.SageBatchId = null;
                        _db.CommissionLedgerRecords.Save(comm);
                    }
                    break;
            }
        }
    }

    // =========================================================================
    // 7. ENTITY RESOLUTION, SELF-LEARNING ASSIGNMENTS & SAGE 50 BASELINE IMPORT
    // =========================================================================

    public ClientInfo? ResolveClientForInvoice(Invoice inv)
    {
        if (!string.IsNullOrEmpty(inv.ProjectId))
        {
            var project = _db.Projects.GetById(inv.ProjectId);
            if (project?.Client != null && !string.IsNullOrWhiteSpace(project.Client.BusinessName))
            {
                return project.Client;
            }
        }

        if (!string.IsNullOrEmpty(inv.ProjectGroupId))
        {
            var group = _db.ProjectGroups.GetById(inv.ProjectGroupId);
            if (group?.Client != null && !string.IsNullOrWhiteSpace(group.Client.BusinessName))
            {
                return group.Client;
            }
        }

        return null;
    }

    public string ResolveSageCustomerName(ClientInfo? client, Sage50ExportConfig config)
    {
        if (client == null) return "Client";
        var rawName = client.BusinessName?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(rawName) &&
            config.CustomerNameMappings.TryGetValue(rawName, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim();
        }

        if (!string.IsNullOrWhiteSpace(client.SageReferenceName))
        {
            return client.SageReferenceName.Trim();
        }

        if (string.IsNullOrEmpty(rawName)) return "Client";

        // Check normalized match in ImportedCustomers
        var match = FindBestPartyMatch(rawName, config.ImportedCustomers);
        return match?.Name ?? rawName;
    }

    public string ResolveSageVendorName(string? vendorName, PartyContactInfo? contactInfo, Sage50ExportConfig config)
    {
        var rawName = !string.IsNullOrWhiteSpace(vendorName)
            ? vendorName.Trim()
            : (contactInfo?.BusinessName?.Trim() ?? string.Empty);

        if (!string.IsNullOrEmpty(rawName) &&
            config.VendorNameMappings.TryGetValue(rawName, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim();
        }

        if (!string.IsNullOrWhiteSpace(contactInfo?.SageReferenceName))
        {
            return contactInfo.SageReferenceName.Trim();
        }

        if (string.IsNullOrEmpty(rawName)) return "Supplier";

        var match = FindBestPartyMatch(rawName, config.ImportedVendors);
        return match?.Name ?? rawName;
    }

    public string ResolveInvoiceGlAccount(Invoice inv, Sage50ExportConfig config)
    {
        if (!string.IsNullOrWhiteSpace(inv.GlAccountCode))
        {
            return inv.GlAccountCode.Trim();
        }

        var client = ResolveClientForInvoice(inv);
        if (!string.IsNullOrWhiteSpace(client?.DefaultGlAccountCode))
        {
            return client.DefaultGlAccountCode.Trim();
        }

        var rawClientName = client?.BusinessName?.Trim();
        if (!string.IsNullOrEmpty(rawClientName) &&
            config.CustomerDefaultGlMappings.TryGetValue(rawClientName, out var mappedGl) &&
            !string.IsNullOrWhiteSpace(mappedGl))
        {
            return mappedGl.Trim();
        }

        return config.DefaultSalesRevenueGl;
    }

    public string ResolvePayableGlAccount(SagePayableExportItem payable, Sage50ExportConfig config)
    {
        if (!string.IsNullOrWhiteSpace(payable.GlAccountCode))
        {
            return payable.GlAccountCode.Trim();
        }

        var supplier = !string.IsNullOrEmpty(payable.SupplierId)
            ? _db.Suppliers.GetById(payable.SupplierId)
            : FindSupplierByName(payable.VendorName);
        if (!string.IsNullOrWhiteSpace(supplier?.ContactInfo?.DefaultGlAccountCode))
        {
            return supplier.ContactInfo.DefaultGlAccountCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payable.VendorName) &&
            config.VendorDefaultGlMappings.TryGetValue(payable.VendorName.Trim(), out var mappedGl) &&
            !string.IsNullOrWhiteSpace(mappedGl))
        {
            return mappedGl.Trim();
        }

        return payable.SourceType switch
        {
            SagePayableSourceType.RecurringBill => !string.IsNullOrWhiteSpace(config.DefaultRecurringExpenseGl) ? config.DefaultRecurringExpenseGl : config.DefaultExpenseGl,
            SagePayableSourceType.ExpenseReport => !string.IsNullOrWhiteSpace(config.DefaultEmployeeExpenseGl) ? config.DefaultEmployeeExpenseGl : config.DefaultExpenseGl,
            SagePayableSourceType.Commission => !string.IsNullOrWhiteSpace(config.DefaultCommissionGl) ? config.DefaultCommissionGl : config.DefaultExpenseGl,
            _ => config.DefaultExpenseGl
        };
    }

    public void AssignInvoiceGlAndCustomer(Invoice invoice, string? glAccountCode, string? sageCustomerName, bool rememberForClient = true)
    {
        var profile = _db.CompanyProfile.Get();
        var config = profile.Accounting.SageSettings ??= new Sage50ExportConfig();

        var cleanGl = string.IsNullOrWhiteSpace(glAccountCode) ? null : glAccountCode.Trim();
        var cleanCustomer = string.IsNullOrWhiteSpace(sageCustomerName) ? null : sageCustomerName.Trim();

        if (cleanGl != null)
        {
            invoice.GlAccountCode = cleanGl;
            _db.Invoices.Save(invoice);
        }

        if (rememberForClient)
        {
            bool configChanged = false;
            var client = ResolveClientForInvoice(invoice);
            var clientKey = client?.BusinessName?.Trim();

            if (!string.IsNullOrEmpty(clientKey))
            {
                if (cleanGl != null)
                {
                    config.CustomerDefaultGlMappings[clientKey] = cleanGl;
                    configChanged = true;
                }
                if (cleanCustomer != null)
                {
                    config.CustomerNameMappings[clientKey] = cleanCustomer;
                    configChanged = true;
                }
            }

            if (!string.IsNullOrEmpty(invoice.ProjectId))
            {
                var proj = _db.Projects.GetById(invoice.ProjectId);
                if (proj?.Client != null)
                {
                    if (cleanGl != null) proj.Client.DefaultGlAccountCode = cleanGl;
                    if (cleanCustomer != null) proj.Client.SageReferenceName = cleanCustomer;
                    _db.Projects.Save(proj);
                }
            }

            if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
            {
                var grp = _db.ProjectGroups.GetById(invoice.ProjectGroupId);
                if (grp?.Client != null)
                {
                    if (cleanGl != null) grp.Client.DefaultGlAccountCode = cleanGl;
                    if (cleanCustomer != null) grp.Client.SageReferenceName = cleanCustomer;
                    _db.ProjectGroups.Save(grp);
                }
            }

            if (configChanged)
            {
                _db.CompanyProfile.Save(profile);
            }
        }
    }

    public void AssignPayableGlAndVendor(SagePayableExportItem payable, string? glAccountCode, string? sageVendorName, bool rememberForVendor = true)
    {
        var profile = _db.CompanyProfile.Get();
        var config = profile.Accounting.SageSettings ??= new Sage50ExportConfig();

        var cleanGl = string.IsNullOrWhiteSpace(glAccountCode) ? null : glAccountCode.Trim();
        var cleanVendor = string.IsNullOrWhiteSpace(sageVendorName) ? null : sageVendorName.Trim();

        if (cleanGl != null)
        {
            payable.GlAccountCode = cleanGl;
            var lineCode = ResolveDetailLineCode(cleanGl, payable.SageItemCode, isSales: false, config);
            payable.LineItems = payable.LineItems
                .Select(li => (li.Description, li.Quantity, li.RawLineTotal, (string?)lineCode))
                .ToList();
        }

        switch (payable.SourceType)
        {
            case SagePayableSourceType.SupplierBill:
                var bill = payable.UnderlyingEntity as Bill ?? _db.Bills.GetById(payable.Id);
                if (bill != null)
                {
                    if (cleanGl != null) bill.GlAccountCode = cleanGl;
                    _db.Bills.Save(bill);
                }
                break;

            case SagePayableSourceType.RecurringBill:
                var recProfile = payable.UnderlyingEntity as RecurringExpense
                    ?? (!string.IsNullOrEmpty(payable.ParentEntityId) ? _db.RecurringExpenses.GetById(payable.ParentEntityId) : null);
                var record = payable.SecondaryEntity as RecurringBillRecord
                    ?? recProfile?.History.FirstOrDefault(h => h.Id == payable.Id);
                if (recProfile != null)
                {
                    if (cleanGl != null)
                    {
                        if (record != null) record.GlAccountCode = cleanGl;
                        if (rememberForVendor) recProfile.GlAccountCode = cleanGl;
                    }
                    _db.RecurringExpenses.Save(recProfile);
                }
                break;

            case SagePayableSourceType.ExpenseReport:
                var report = payable.UnderlyingEntity as ExpenseReport ?? _db.ExpenseReports.GetById(payable.Id);
                if (report != null && cleanGl != null)
                {
                    foreach (var li in report.LineItems)
                    {
                        li.GlAccountCode = cleanGl;
                    }
                    _db.ExpenseReports.Save(report);
                }
                break;

            case SagePayableSourceType.Commission:
                var comm = payable.UnderlyingEntity as CommissionLedgerRecord ?? _db.CommissionLedgerRecords.GetById(payable.Id);
                if (comm != null && cleanGl != null)
                {
                    comm.GlAccountCode = cleanGl;
                    _db.CommissionLedgerRecords.Save(comm);
                }
                break;
        }

        if (rememberForVendor && !string.IsNullOrWhiteSpace(payable.VendorName))
        {
            var vKey = payable.VendorName.Trim();
            bool configChanged = false;

            if (cleanGl != null)
            {
                config.VendorDefaultGlMappings[vKey] = cleanGl;
                configChanged = true;
            }
            if (cleanVendor != null)
            {
                config.VendorNameMappings[vKey] = cleanVendor;
                configChanged = true;
            }

            var supplier = !string.IsNullOrEmpty(payable.SupplierId)
                ? _db.Suppliers.GetById(payable.SupplierId)
                : FindSupplierByName(vKey);

            if (supplier != null)
            {
                if (cleanGl != null) supplier.ContactInfo.DefaultGlAccountCode = cleanGl;
                if (cleanVendor != null) supplier.ContactInfo.SageReferenceName = cleanVendor;
                _db.Suppliers.Save(supplier);
            }

            if (configChanged)
            {
                _db.CompanyProfile.Save(profile);
            }
        }
    }

    public void UpdatePayableTaxBreakdown(SagePayableExportItem payable, bool isTaxExempt, decimal subTotal, decimal tpsAmount, decimal tvqAmount)
    {
        decimal sign = payable.IsCreditNote ? -1m : 1m;
        decimal absSubTotal = Math.Round(Math.Abs(subTotal), 2, MidpointRounding.AwayFromZero);
        decimal absTps = isTaxExempt ? 0m : Math.Round(Math.Abs(tpsAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTvq = isTaxExempt ? 0m : Math.Round(Math.Abs(tvqAmount), 2, MidpointRounding.AwayFromZero);
        decimal absTax = absTps + absTvq;
        decimal absTotal = absSubTotal + absTax;

        payable.IsTaxExempt = isTaxExempt;
        payable.SubTotal = absSubTotal * sign;
        payable.TpsAmount = absTps * sign;
        payable.TvqAmount = absTvq * sign;
        payable.TaxAmount = absTax * sign;
        payable.TotalAmount = absTotal * sign;
        payable.HasExplicitTaxBreakdown = true;

        switch (payable.SourceType)
        {
            case SagePayableSourceType.SupplierBill:
                var bill = payable.UnderlyingEntity as Bill ?? _db.Bills.GetById(payable.Id);
                if (bill != null)
                {
                    bill.IsTaxExempt = isTaxExempt;
                    bill.SubTotal = absSubTotal;
                    bill.TpsAmount = absTps;
                    bill.TvqAmount = absTvq;
                    bill.TaxAmount = absTax;
                    bill.Amount = absTotal;
                    _db.Bills.Save(bill);
                }
                break;

            case SagePayableSourceType.RecurringBill:
                var recProfile = payable.UnderlyingEntity as RecurringExpense
                    ?? (!string.IsNullOrEmpty(payable.ParentEntityId) ? _db.RecurringExpenses.GetById(payable.ParentEntityId) : null);
                var record = payable.SecondaryEntity as RecurringBillRecord
                    ?? recProfile?.History.FirstOrDefault(h => h.Id == payable.Id);
                if (recProfile != null && record != null)
                {
                    record.IsTaxExempt = isTaxExempt;
                    record.SubTotal = absSubTotal;
                    record.TpsAmount = absTps;
                    record.TvqAmount = absTvq;
                    record.TaxAmount = absTax;
                    record.Amount = absTotal;
                    _db.RecurringExpenses.Save(recProfile);
                }
                break;

            case SagePayableSourceType.ExpenseReport:
                var report = payable.UnderlyingEntity as ExpenseReport ?? _db.ExpenseReports.GetById(payable.Id);
                if (report != null)
                {
                    foreach (var li in report.LineItems)
                    {
                        li.IsTaxExempt = isTaxExempt;
                        if (isTaxExempt)
                        {
                            li.SubTotal = li.Total;
                            li.TpsAmount = 0m;
                            li.TvqAmount = 0m;
                        }
                    }
                    _db.ExpenseReports.Save(report);
                }
                break;

            case SagePayableSourceType.Commission:
                var comm = payable.UnderlyingEntity as CommissionLedgerRecord ?? _db.CommissionLedgerRecords.GetById(payable.Id);
                if (comm != null)
                {
                    comm.ApplyTaxes = !isTaxExempt;
                    comm.CommissionAmount = absSubTotal;
                    comm.TpsAmount = absTps;
                    comm.TvqAmount = absTvq;
                    _db.CommissionLedgerRecords.Save(comm);
                }
                break;
        }
    }

    public SageBaselineImportSummary ImportSageBaselineBytes(byte[] fileBytes, Sage50ExportConfig config, bool syncSuppliersToDatabase = true)
    {
        if (fileBytes == null || fileBytes.Length == 0)
        {
            return new SageBaselineImportSummary { Errors = ["Uploaded file is empty."] };
        }

        // Auto-detect UTF-8 vs Windows-1252 / Latin-1 (ANSI)
        string content;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            content = strictUtf8.GetString(fileBytes);
        }
        catch (DecoderFallbackException)
        {
            content = Encoding.Latin1.GetString(fileBytes);
        }

        return ImportSageBaselineFile(content, config, syncSuppliersToDatabase);
    }

    public SageBaselineImportSummary ImportSageBaselineFile(string fileContent, Sage50ExportConfig config, bool syncSuppliersToDatabase = true)
    {
        var summary = new SageBaselineImportSummary();
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            summary.Errors.Add("File content is empty.");
            return summary;
        }

        var lines = fileContent
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("<!", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
        {
            summary.Errors.Add("No valid lines found in the file.");
            return summary;
        }

        // Case A: .IMP file containing <customers> and/or <vendors> blocks
        if (lines.Any(l => l.StartsWith("<customers>", StringComparison.OrdinalIgnoreCase) ||
                           l.StartsWith("<vendors>", StringComparison.OrdinalIgnoreCase)))
        {
            ParseImpMasterBlocks(lines, config, summary);
        }
        else
        {
            // Case B: Sage 50 Export Records CSV/TXT format (Row 1: <Version>, <Country>, <RecordType>)
            var firstRow = ParseCsvLine(lines[0]);
            string recordType = DetectSageRecordType(firstRow, lines.Count > 1 ? ParseCsvLine(lines[1]) : []);
            summary.DetectedRecordType = recordType;

            int dataStartIndex = DetermineDataStartIndex(lines, firstRow);
            if (firstRow.Count >= 2 && IsDigitsOnly(firstRow[0]) && firstRow[0].Length >= 4)
            {
                config.VersionHeader = firstRow[0];
                if (firstRow[1] is "1" or "2" or "3" or "5" or "7")
                {
                    config.CountryCode = firstRow[1];
                }
            }

            switch (recordType)
            {
                case "Customers":
                    ParseSagePartyRows(lines, dataStartIndex, config.ImportedCustomers, isCustomer: true, summary);
                    break;
                case "Vendors":
                    ParseSagePartyRows(lines, dataStartIndex, config.ImportedVendors, isCustomer: false, summary);
                    break;
                case "Accounts":
                    ParseSageAccountRows(lines, dataStartIndex, config, summary);
                    break;
                case "Inventory":
                    ParseSageInventoryRows(lines, dataStartIndex, config, summary);
                    break;
                default:
                    summary.Errors.Add("Unrecognized Sage 50 export format. Expected Customers, Vendors, Accounts, or Inventory & Services.");
                    return summary;
            }
        }

        config.LastBaselineImportAt = DateTime.UtcNow;
        summary.AutoMappedEntities = AutoMapEntitiesToBaseline(config, syncSuppliersToDatabase);
        if (syncSuppliersToDatabase && summary.VendorsImported > 0)
        {
            summary.SuppliersSyncedToSpokes = SyncImportedVendorsToSuppliers(config);
        }

        summary.Success = summary.Errors.Count == 0;
        return summary;
    }

    public int AutoMapEntitiesToBaseline(Sage50ExportConfig config, bool syncSuppliersToDatabase = true)
    {
        int mappedCount = 0;

        // 1. Auto-map Project & ProjectGroup Clients against ImportedCustomers
        if (config.ImportedCustomers.Count > 0)
        {
            foreach (var project in _db.Projects.GetAll())
            {
                if (TryAutoMapClient(project.Client, config))
                {
                    _db.Projects.Save(project);
                    mappedCount++;
                }
            }

            foreach (var group in _db.ProjectGroups.GetAll())
            {
                if (TryAutoMapClient(group.Client, config))
                {
                    _db.ProjectGroups.Save(group);
                    mappedCount++;
                }
            }
        }

        // 2. Auto-map Suppliers, Bills, RecurringExpenses, Employees, and Commissions against ImportedVendors
        if (config.ImportedVendors.Count > 0)
        {
            foreach (var supplier in _db.Suppliers.GetAll())
            {
                var rawName = supplier.ContactInfo.BusinessName?.Trim();
                if (string.IsNullOrEmpty(rawName)) continue;

                var match = FindBestPartyMatch(rawName, config.ImportedVendors);
                if (match != null)
                {
                    config.VendorNameMappings[rawName] = match.Name;
                    if (supplier.ContactInfo.SageReferenceName != match.Name)
                    {
                        supplier.ContactInfo.SageReferenceName = match.Name;
                        if (syncSuppliersToDatabase)
                        {
                            _db.Suppliers.Save(supplier);
                        }
                    }
                    mappedCount++;
                }
            }

            var distinctVendorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in _db.Bills.GetAll())
            {
                if (!string.IsNullOrWhiteSpace(b.SupplierName)) distinctVendorNames.Add(b.SupplierName.Trim());
            }
            foreach (var r in _db.RecurringExpenses.GetAll().Where(x => !x.IsEstimate))
            {
                if (!string.IsNullOrWhiteSpace(r.Supplier)) distinctVendorNames.Add(r.Supplier.Trim());
                else if (!string.IsNullOrWhiteSpace(r.Name)) distinctVendorNames.Add(r.Name.Trim());
            }
            foreach (var exp in _db.ExpenseReports.GetAll())
            {
                if (!string.IsNullOrWhiteSpace(exp.EmployeeName)) distinctVendorNames.Add(exp.EmployeeName.Trim());
            }
            foreach (var comm in _db.CommissionLedgerRecords.GetAll())
            {
                if (!string.IsNullOrWhiteSpace(comm.BeneficiaryName)) distinctVendorNames.Add(comm.BeneficiaryName.Trim());
            }

            foreach (var vendorName in distinctVendorNames)
            {
                if (config.VendorNameMappings.ContainsKey(vendorName)) continue;
                var match = FindBestPartyMatch(vendorName, config.ImportedVendors);
                if (match != null)
                {
                    config.VendorNameMappings[vendorName] = match.Name;
                    mappedCount++;
                }
            }
        }

        return mappedCount;
    }

    private int SyncImportedVendorsToSuppliers(Sage50ExportConfig config)
    {
        int synced = 0;
        var existingSuppliers = _db.Suppliers.GetAll().ToList();

        foreach (var vendor in config.ImportedVendors)
        {
            if (string.IsNullOrWhiteSpace(vendor.Name)) continue;

            var existing = existingSuppliers.FirstOrDefault(s =>
                string.Equals(s.ContactInfo.SageReferenceName, vendor.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeKey(s.ContactInfo.BusinessName), NormalizeKey(vendor.Name), StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                bool changed = false;
                if (existing.ContactInfo.SageReferenceName != vendor.Name)
                {
                    existing.ContactInfo.SageReferenceName = vendor.Name;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(existing.ContactInfo.BusinessAddressStreet) && !string.IsNullOrWhiteSpace(vendor.Street1))
                {
                    existing.ContactInfo.BusinessAddressStreet = vendor.Street1;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(existing.ContactInfo.BusinessAddressCity) && !string.IsNullOrWhiteSpace(vendor.City))
                {
                    existing.ContactInfo.BusinessAddressCity = vendor.City;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(existing.ContactInfo.BusinessAddressZip) && !string.IsNullOrWhiteSpace(vendor.PostalCode))
                {
                    existing.ContactInfo.BusinessAddressZip = vendor.PostalCode;
                    changed = true;
                }
                if (changed)
                {
                    _db.Suppliers.Save(existing);
                    synced++;
                }
            }
            else
            {
                var newSupplier = new Supplier
                {
                    ContactInfo = new PartyContactInfo
                    {
                        BusinessName = vendor.Name,
                        SageReferenceName = vendor.Name,
                        ContactPersonName = vendor.Contact,
                        BusinessAddressStreet = vendor.Street1,
                        BusinessAddressCity = vendor.City,
                        BusinessAddressState = !string.IsNullOrWhiteSpace(vendor.Province) ? vendor.Province : "QC",
                        BusinessAddressZip = vendor.PostalCode,
                        BusinessAddressCountry = !string.IsNullOrWhiteSpace(vendor.Country) ? vendor.Country : "Canada",
                        ContactPersonPhone = vendor.Phone1,
                        ContactPersonEmail = vendor.Email
                    }
                };
                _db.Suppliers.Save(newSupplier);
                existingSuppliers.Add(newSupplier);
                synced++;
            }

            config.VendorNameMappings[vendor.Name] = vendor.Name;
        }

        return synced;
    }

    private bool TryAutoMapClient(ClientInfo? client, Sage50ExportConfig config)
    {
        if (client == null || string.IsNullOrWhiteSpace(client.BusinessName)) return false;
        var rawName = client.BusinessName.Trim();
        var match = FindBestPartyMatch(rawName, config.ImportedCustomers);
        if (match == null) return false;

        config.CustomerNameMappings[rawName] = match.Name;
        if (client.SageReferenceName != match.Name)
        {
            client.SageReferenceName = match.Name;
            return true;
        }
        return false;
    }

    // =========================================================================
    // 8. BASELINE PARSING HELPERS
    // =========================================================================

    private static void ParseImpMasterBlocks(List<string> lines, Sage50ExportConfig config, SageBaselineImportSummary summary)
    {
        string? currentTag = null;
        foreach (var line in lines)
        {
            if (line.Equals("<customers>", StringComparison.OrdinalIgnoreCase))
            {
                currentTag = "Customers";
                continue;
            }
            if (line.Equals("</customers>", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("</vendors>", StringComparison.OrdinalIgnoreCase))
            {
                currentTag = null;
                continue;
            }
            if (line.Equals("<vendors>", StringComparison.OrdinalIgnoreCase))
            {
                currentTag = "Vendors";
                continue;
            }

            if (currentTag is "Customers" or "Vendors")
            {
                var cols = ParseCsvLine(line);
                if (cols.Count == 0 || string.IsNullOrWhiteSpace(cols[0])) continue;

                // .IMP <customers>/<vendors> layout:
                // "name", "0", "Contact", "street", "street2", "city", "prov", "postal", "country", "phone", "phone2", "fax", "email"
                bool hasZeroFlag = cols.Count > 1 && cols[1] == "0";
                int offset = hasZeroFlag ? 1 : 0;

                var record = new SagePartyRecord
                {
                    Name = cols[0].Trim(),
                    Contact = GetCol(cols, 1 + offset),
                    Street1 = GetCol(cols, 2 + offset),
                    Street2 = GetCol(cols, 3 + offset),
                    City = GetCol(cols, 4 + offset),
                    Province = GetCol(cols, 5 + offset, "QC"),
                    PostalCode = GetCol(cols, 6 + offset),
                    Country = GetCol(cols, 7 + offset, "Canada"),
                    Phone1 = GetCol(cols, 8 + offset),
                    Phone2 = GetCol(cols, 9 + offset),
                    Fax = GetCol(cols, 10 + offset),
                    Email = GetCol(cols, 11 + offset)
                };

                if (currentTag == "Customers")
                {
                    UpsertPartyRecord(config.ImportedCustomers, record);
                    summary.CustomersImported++;
                }
                else
                {
                    UpsertPartyRecord(config.ImportedVendors, record);
                    summary.VendorsImported++;
                }
            }
        }
        summary.DetectedRecordType = summary.CustomersImported > 0 && summary.VendorsImported > 0
            ? "Customers & Vendors (.imp)"
            : (summary.CustomersImported > 0 ? "Customers" : "Vendors");
    }

    private static string DetectSageRecordType(List<string> row1, List<string> row2)
    {
        if (row1.Count >= 3)
        {
            var token = row1[2].Trim();
            if (token.Contains("Customer", StringComparison.OrdinalIgnoreCase) || token.Contains("Client", StringComparison.OrdinalIgnoreCase))
                return "Customers";
            if (token.Contains("Vendor", StringComparison.OrdinalIgnoreCase) || token.Contains("Supplier", StringComparison.OrdinalIgnoreCase) || token.Contains("Fournisseur", StringComparison.OrdinalIgnoreCase))
                return "Vendors";
            if (token.Contains("Account", StringComparison.OrdinalIgnoreCase) || token.Contains("Compte", StringComparison.OrdinalIgnoreCase))
                return "Accounts";
            if (token.Contains("Inventory", StringComparison.OrdinalIgnoreCase) || token.Contains("Item", StringComparison.OrdinalIgnoreCase) || token.Contains("Stock", StringComparison.OrdinalIgnoreCase) || token.Contains("Article", StringComparison.OrdinalIgnoreCase))
                return "Inventory";
        }

        var headerJoined = string.Join(" ", row1.Concat(row2));
        if (headerJoined.Contains("Account Number", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Account Type", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("GIFI", StringComparison.OrdinalIgnoreCase))
            return "Accounts";
        if (headerJoined.Contains("Linked Revenue", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Linked Expense", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Selling Price", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Item ID", StringComparison.OrdinalIgnoreCase))
            return "Inventory";
        if (headerJoined.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Supplier", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Fournisseur", StringComparison.OrdinalIgnoreCase))
            return "Vendors";
        if (headerJoined.Contains("Customer", StringComparison.OrdinalIgnoreCase) ||
            headerJoined.Contains("Client", StringComparison.OrdinalIgnoreCase))
            return "Customers";

        return string.Empty;
    }

    private static int DetermineDataStartIndex(List<string> lines, List<string> firstRow)
    {
        int idx = 0;
        if (firstRow.Count >= 2 && IsDigitsOnly(firstRow[0]) && firstRow[0].Length >= 4)
        {
            idx = 1;
        }

        if (idx < lines.Count)
        {
            var candidateHeader = ParseCsvLine(lines[idx]);
            if (candidateHeader.Count > 0)
            {
                var firstCell = candidateHeader[0];
                if (firstCell.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                    firstCell.Contains("Account", StringComparison.OrdinalIgnoreCase) ||
                    firstCell.Contains("Number", StringComparison.OrdinalIgnoreCase) ||
                    firstCell.Contains("Item", StringComparison.OrdinalIgnoreCase) ||
                    firstCell.Contains("Nom", StringComparison.OrdinalIgnoreCase) ||
                    firstCell.Contains("Numéro", StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                }
            }
        }

        return idx;
    }

    private static void ParseSagePartyRows(
        List<string> lines,
        int startIndex,
        List<SagePartyRecord> targetList,
        bool isCustomer,
        SageBaselineImportSummary summary)
    {
        for (int i = startIndex; i < lines.Count; i++)
        {
            var cols = ParseCsvLine(lines[i]);
            if (cols.Count == 0 || string.IsNullOrWhiteSpace(cols[0])) continue;

            // Standard Sage 50 Export Records CSV layout:
            // 0: Name, 1: Contact, 2: Street1, 3: Street2, 4: City, 5: Province, 6: Country (or Postal), 7: Postal (or Country), 8: Phone1, 9: Phone2, 10: Fax, 11: Email, 12: Website
            string col6 = GetCol(cols, 6);
            string col7 = GetCol(cols, 7);
            bool col6IsPostal = LooksLikePostalCode(col6);

            var record = new SagePartyRecord
            {
                Name = cols[0].Trim(),
                Contact = GetCol(cols, 1),
                Street1 = GetCol(cols, 2),
                Street2 = GetCol(cols, 3),
                City = GetCol(cols, 4),
                Province = GetCol(cols, 5, "QC"),
                PostalCode = col6IsPostal ? col6 : col7,
                Country = col6IsPostal ? (string.IsNullOrWhiteSpace(col7) ? "Canada" : col7) : (string.IsNullOrWhiteSpace(col6) ? "Canada" : col6),
                Phone1 = GetCol(cols, 8),
                Phone2 = GetCol(cols, 9),
                Fax = GetCol(cols, 10),
                Email = GetCol(cols, 11),
                Website = GetCol(cols, 12)
            };

            UpsertPartyRecord(targetList, record);
            if (isCustomer) summary.CustomersImported++;
            else summary.VendorsImported++;
        }
    }

    private static void ParseSageAccountRows(List<string> lines, int startIndex, Sage50ExportConfig config, SageBaselineImportSummary summary)
    {
        for (int i = startIndex; i < lines.Count; i++)
        {
            var cols = ParseCsvLine(lines[i]);
            if (cols.Count < 2 || string.IsNullOrWhiteSpace(cols[0])) continue;

            var acctNum = cols[0].Trim();
            var record = new SageGlAccountRecord
            {
                AccountNumber = acctNum,
                AccountName = GetCol(cols, 1),
                AccountType = GetCol(cols, 2),
                AccountClass = GetCol(cols, 3),
                AccountNameFr = GetCol(cols, 6)
            };

            var existingIdx = config.ImportedAccounts.FindIndex(a =>
                string.Equals(a.AccountNumber, acctNum, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0) config.ImportedAccounts[existingIdx] = record;
            else config.ImportedAccounts.Add(record);

            summary.AccountsImported++;
        }

        if (config.ImportedAccounts.Count > 0)
        {
            AutoDetectControlAndDefaultGlAccounts(config);
        }
    }

    private static void AutoDetectControlAndDefaultGlAccounts(Sage50ExportConfig config)
    {
        config.AccountsReceivableGl = ResolveValidOrBestGl(
            config.AccountsReceivableGl,
            config.ImportedAccounts,
            ["Accounts Receivable", "Comptes clients", "Receivable", "Clients"],
            "1");

        config.AccountsPayableGl = ResolveValidOrBestGl(
            config.AccountsPayableGl,
            config.ImportedAccounts,
            ["Accounts Payable", "Comptes fournisseurs", "Payable", "Fournisseurs"],
            "2");

        config.TpsTaxAccount = ResolveValidOrBestGl(
            config.TpsTaxAccount,
            config.ImportedAccounts,
            ["TPS", "GST", "Taxe sur les produits"],
            "2");

        config.TvqTaxAccount = ResolveValidOrBestGl(
            config.TvqTaxAccount,
            config.ImportedAccounts,
            ["TVQ", "QST", "Taxe de vente"],
            "2");

        config.DefaultSalesRevenueGl = ResolveValidOrBestGl(
            config.DefaultSalesRevenueGl,
            config.ImportedAccounts,
            ["Sales", "Revenue", "Ventes", "Revenus", "Services", "Honoraires"],
            "4");

        config.DefaultExpenseGl = ResolveValidOrBestGl(
            config.DefaultExpenseGl,
            config.ImportedAccounts,
            ["Purchases", "Materials", "Cost of Goods", "Subcontracts", "Achats", "Matériaux", "Materiaux", "Sous-traitance", "Expense"],
            "5");

        config.DefaultRecurringExpenseGl = ResolveValidOrBestGl(
            config.DefaultRecurringExpenseGl,
            config.ImportedAccounts,
            ["Utilities", "Telecom", "Telephone", "Internet", "Rent", "Hydro", "Électricité", "Electricite", "Loyer", "Télécommunications"],
            "5",
            fallbackAccount: config.DefaultExpenseGl);

        config.DefaultEmployeeExpenseGl = ResolveValidOrBestGl(
            config.DefaultEmployeeExpenseGl,
            config.ImportedAccounts,
            ["Travel", "Employee Expense", "Meals", "Déplacement", "Deplacement", "Frais de voyage", "Remboursement"],
            "5",
            fallbackAccount: config.DefaultExpenseGl);

        config.DefaultKilometrageGl = ResolveValidOrBestGl(
            config.DefaultKilometrageGl,
            config.ImportedAccounts,
            ["Kilometrage", "Mileage", "Automobile", "Vehicle", "Kilométrage", "Véhicule", "Vehicule"],
            "5",
            fallbackAccount: config.DefaultEmployeeExpenseGl);

        config.DefaultCommissionGl = ResolveValidOrBestGl(
            config.DefaultCommissionGl,
            config.ImportedAccounts,
            ["Commission", "Commissions"],
            "5",
            fallbackAccount: config.DefaultExpenseGl);
    }

    private static string ResolveValidOrBestGl(
        string currentGl,
        List<SageGlAccountRecord> accounts,
        string[] keywords,
        string preferredLeadingDigit,
        string? fallbackAccount = null)
    {
        if (!string.IsNullOrWhiteSpace(currentGl) &&
            accounts.Any(a => string.Equals(a.AccountNumber.Trim(), currentGl.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return currentGl.Trim();
        }

        foreach (var kw in keywords)
        {
            var match = accounts.FirstOrDefault(a =>
                (a.AccountName?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.AccountNameFr?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.AccountClass?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true));

            if (match != null && !string.IsNullOrWhiteSpace(match.AccountNumber))
            {
                return match.AccountNumber.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackAccount) &&
            accounts.Any(a => string.Equals(a.AccountNumber.Trim(), fallbackAccount.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return fallbackAccount.Trim();
        }

        var prefixMatch = accounts.FirstOrDefault(a =>
            a.AccountNumber.Trim().StartsWith(preferredLeadingDigit, StringComparison.OrdinalIgnoreCase));
        if (prefixMatch != null)
        {
            return prefixMatch.AccountNumber.Trim();
        }

        return currentGl;
    }

    private static void ParseSageInventoryRows(List<string> lines, int startIndex, Sage50ExportConfig config, SageBaselineImportSummary summary)
    {
        for (int i = startIndex; i < lines.Count; i++)
        {
            var cols = ParseCsvLine(lines[i]);
            if (cols.Count < 2 || string.IsNullOrWhiteSpace(cols[0])) continue;

            var itemId = cols[0].Trim();
            var record = new SageServiceItemRecord
            {
                ItemId = itemId,
                Description = GetCol(cols, 1),
                ItemType = GetCol(cols, 2, "Service"),
                Unit = GetCol(cols, 3),
                LinkedRevenueGl = GetCol(cols, 7),
                LinkedExpenseGl = GetCol(cols, 8),
                DescriptionFr = GetCol(cols, 10)
            };

            var existingIdx = config.ImportedServiceItems.FindIndex(s =>
                string.Equals(s.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0) config.ImportedServiceItems[existingIdx] = record;
            else config.ImportedServiceItems.Add(record);

            summary.ServiceItemsImported++;
        }
    }

    private static void UpsertPartyRecord(List<SagePartyRecord> list, SagePartyRecord record)
    {
        var idx = list.FindIndex(p => string.Equals(p.Name, record.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) list[idx] = record;
        else list.Add(record);
    }

    private static SagePartyRecord? FindBestPartyMatch(string rawName, List<SagePartyRecord> candidates)
    {
        if (string.IsNullOrWhiteSpace(rawName) || candidates.Count == 0) return null;

        var exact = candidates.FirstOrDefault(c => string.Equals(c.Name, rawName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var normTarget = NormalizeKey(rawName);
        if (string.IsNullOrEmpty(normTarget)) return null;

        var normMatch = candidates.FirstOrDefault(c => string.Equals(NormalizeKey(c.Name), normTarget, StringComparison.OrdinalIgnoreCase));
        if (normMatch != null) return normMatch;

        // Prefix / contains match if at least 5 chars
        if (normTarget.Length >= 5)
        {
            return candidates.FirstOrDefault(c =>
            {
                var normCand = NormalizeKey(c.Name);
                return normCand.Length >= 5 && (normCand.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase) ||
                                                normTarget.StartsWith(normCand, StringComparison.OrdinalIgnoreCase));
            });
        }

        return null;
    }

    private Supplier? FindSupplierByName(string? vendorName)
    {
        if (string.IsNullOrWhiteSpace(vendorName)) return null;
        var all = _db.Suppliers.GetAll();
        return all.FirstOrDefault(s =>
            string.Equals(s.ContactInfo.BusinessName, vendorName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.ContactInfo.SageReferenceName, vendorName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var formD = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        var normalized = sb.ToString();
        // Strip common corporate suffixes for fuzzy matching
        foreach (var suffix in new[] { "incorporate", "inc", "ltdd", "ltd", "ltee", "corp", "corporation", "llc", "enr" })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length + 2)
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        return normalized;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) return result;

        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString().Trim());
        return result;
    }

    private static string GetCol(List<string> cols, int index, string fallback = "")
    {
        if (index >= 0 && index < cols.Count && !string.IsNullOrWhiteSpace(cols[index]))
        {
            return cols[index].Trim();
        }
        return fallback;
    }

    private static bool IsDigitsOnly(string value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsDigit);

    private static bool LooksLikePostalCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var clean = value.Replace(" ", "").Replace("-", "");
        if (clean.Length == 6 && char.IsLetter(clean[0]) && char.IsDigit(clean[1]) && char.IsLetter(clean[2]))
            return true;
        if (clean.Length is 5 or 9 && clean.All(char.IsDigit))
            return true;
        return false;
    }

    private static void AppendImpLine(StringBuilder sb, string line)
    {
        sb.Append(line).Append("\r\n");
    }

    private static string EscapeImp(string? value, int maxLength = 0)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = value.Replace("\r", "").Replace("\n", " ").Replace("\t", " ").Trim();
        if (maxLength > 0 && clean.Length > maxLength)
        {
            clean = clean[..maxLength].Trim();
        }
        return clean.Replace("\"", "\"\"");
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
