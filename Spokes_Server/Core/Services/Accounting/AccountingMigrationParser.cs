namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;

public sealed class ParsedGlAccountRow
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string SubType { get; set; } = string.Empty;
    public string ClassLabel { get; set; } = string.Empty;
    public string StandardTaxCode { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public bool HasExplicitDebitCredit { get; set; }
    public decimal? RawSingleBalance { get; set; }
    public bool IsLikelyObsolete { get; set; }
}

public sealed class ParsedPartyRow
{
    public bool IsCustomer { get; set; }
    public string SourceCode { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string DefaultCurrency { get; set; } = string.Empty;
    public PartyContactInfo ContactInfo { get; set; } = new();
}

public sealed class ParsedProjectRow
{
    public string RawName { get; set; } = string.Empty;
    public string ExtractedCode { get; set; } = string.Empty;
    public string CleanName { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public bool IsInternalBucket { get; set; }
}

public sealed class ParsedSubledgerRow
{
    public bool IsReceivable { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string PartyName { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime DueDate { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal OpenAmount { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class ParsedPayrollYtdRow
{
    public string EmployeeName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Street1 { get; set; } = string.Empty;
    public string Street2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string SecondaryPhone { get; set; } = string.Empty;
    public string SocialInsuranceNumber { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    public DateOnly? HireDate { get; set; }
    public int PayPeriodsPerYear { get; set; } = 26;
    public bool FromEmployeeDirectoryFile { get; set; }
    public bool HasDuplicateSinWarning { get; set; }
    public string? DuplicateMergedFromName { get; set; }
    public decimal GrossPay { get; set; }
    public decimal Vacation { get; set; }
    public decimal FederalTax { get; set; }
    public decimal RegionalTax { get; set; }
    public decimal QppCppEmployee { get; set; }
    public decimal QppCppEmployer { get; set; }
    public decimal Qpp2Employee { get; set; }
    public decimal Qpp2Employer { get; set; }
    public decimal EiEmployee { get; set; }
    public decimal EiEmployer { get; set; }
    public decimal QpipEmployee { get; set; }
    public decimal QpipEmployer { get; set; }
}

public sealed class ParsedMigrationDataset
{
    public AccountingMigrationSourceFormat DetectedFormat { get; set; } = AccountingMigrationSourceFormat.StandardCsv;
    public List<ParsedGlAccountRow> GlRows { get; set; } = [];
    public List<ParsedPartyRow> Vendors { get; set; } = [];
    public List<ParsedPartyRow> Customers { get; set; } = [];
    public List<ParsedProjectRow> Projects { get; set; } = [];
    public List<ParsedSubledgerRow> OpenInvoices { get; set; } = [];
    public List<ParsedSubledgerRow> OpenBills { get; set; } = [];
    public List<ParsedPayrollYtdRow> PayrollYtds { get; set; } = [];
    public List<string> InformationalNotices { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Multi-format parser supporting Sage 50 Desktop (.imp &amp; Export Records CSV), Sage Business Cloud CSV,
/// and standard accounting CSV exports with strict UTF-8 -> Windows-1252 encoding fallback.
/// </summary>
public static partial class AccountingMigrationParser
{
    static AccountingMigrationParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [GeneratedRegex(@"^<(/?[A-Za-z0-9_]+)>(.*)$")]
    private static partial Regex ImpSectionTagRegex();

    [GeneratedRegex(@"^\s*\((.+)\)\s*$")]
    private static partial Regex ParenNegativeRegex();

    public static string ReadTextWithSageEncoding(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;

        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(span[3..]);
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(span);
        }
    }

    public static ParsedMigrationDataset ParseFiles(
        IEnumerable<MigrationUploadedFile> files,
        AccountingMigrationSourceFormat requestedFormat,
        DateTime fallbackAsOfDate,
        string defaultCurrency = "CAD")
    {
        var dataset = new ParsedMigrationDataset
        {
            DetectedFormat = requestedFormat == AccountingMigrationSourceFormat.AutoDetect
                ? AccountingMigrationSourceFormat.StandardCsv
                : requestedFormat
        };

        foreach (var file in files)
        {
            if (file.Content == null || file.Content.Length == 0) continue;

            var text = ReadTextWithSageEncoding(file.Content);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var looksLikeImp = file.ExplicitKind == MigrationFileKind.SageImp ||
                               file.FileName.EndsWith(".imp", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<Version>", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<Vendor>", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<Customer>", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<SalInvoice>", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<PurInvoice>", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("<COA>", StringComparison.OrdinalIgnoreCase);

            if (looksLikeImp)
            {
                if (requestedFormat == AccountingMigrationSourceFormat.AutoDetect)
                    dataset.DetectedFormat = AccountingMigrationSourceFormat.Sage50DesktopImp;
                ParseSageImp(text, dataset, fallbackAsOfDate, defaultCurrency);
                continue;
            }

            ParseDelimitedFile(text, file.FileName, file.ExplicitKind, requestedFormat, dataset, fallbackAsOfDate, defaultCurrency);
        }

        FinalizeSingleColumnTrialBalance(dataset.GlRows);
        DeduplicateGlRows(dataset);
        DeduplicateAndMergeEmployeeRows(dataset);
        return dataset;
    }

    public static bool IsClockifyCsv(byte[] content)
    {
        var text = ReadTextWithSageEncoding(content);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var firstLine = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        var lower = firstLine.ToLowerInvariant();
        return lower.Contains("duration") && (lower.Contains("start date") || lower.Contains("project")) && lower.Contains("user");
    }

    public static MigrationFileKind DetectBannerKind(byte[] content)
    {
        var text = ReadTextWithSageEncoding(content);
        if (string.IsNullOrWhiteSpace(text)) return MigrationFileKind.Auto;
        if (text.Contains("<Version>", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("<COA>", StringComparison.OrdinalIgnoreCase) || text.Contains("<Vendor>", StringComparison.OrdinalIgnoreCase) || text.Contains("<SalInvoice>", StringComparison.OrdinalIgnoreCase)))
        {
            return MigrationFileKind.SageImp;
        }
        var rows = CsvReaderHelper.Parse(text, DetectDelimiter(text)).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).Take(2).ToList();
        if (rows.Count > 0 && TryDetectSageExportBanner(rows[0], out var bannerKind))
            return bannerKind;
        if (IsClockifyCsv(content))
            return MigrationFileKind.ClockifyTimesheets;
        return MigrationFileKind.Auto;
    }

    private static bool TryDetectSageExportBanner(List<string> firstRow, out MigrationFileKind kind)
    {
        kind = MigrationFileKind.Auto;
        if (firstRow.Count < 3) return false;
        if (!int.TryParse(firstRow[0].Trim(), out _) || !int.TryParse(firstRow[1].Trim(), out _)) return false;

        var tag = firstRow[2].Trim().ToLowerInvariant();
        kind = tag switch
        {
            "accounts" or "chart of accounts" => MigrationFileKind.ChartOfAccounts,
            "vendors" or "vendor" or "suppliers" or "supplier" => MigrationFileKind.Vendors,
            "customers" or "customer" or "clients" or "client" => MigrationFileKind.Customers,
            "employees" or "employee" => MigrationFileKind.Employees,
            "project" or "projects" => MigrationFileKind.Projects,
            "inventory" => MigrationFileKind.Inventory,
            _ => MigrationFileKind.Auto
        };
        return kind != MigrationFileKind.Auto;
    }

    private static void ParseSageImp(string text, ParsedMigrationDataset dataset, DateTime fallbackDate, string defaultCurrency)
    {
        using var sr = new StringReader(text);
        string? currentSection = null;
        var sectionLines = new List<string>();

        void FlushSection()
        {
            if (string.IsNullOrEmpty(currentSection) || sectionLines.Count == 0)
            {
                sectionLines.Clear();
                return;
            }

            var tag = currentSection.ToLowerInvariant();
            switch (tag)
            {
                case "coa":
                case "accounts":
                    ParseImpCoaSection(sectionLines, dataset);
                    break;
                case "vendor":
                case "vendors":
                case "supplier":
                case "suppliers":
                    ParseImpPartySection(sectionLines, isCustomer: false, dataset);
                    break;
                case "customer":
                case "customers":
                case "client":
                case "clients":
                    ParseImpPartySection(sectionLines, isCustomer: true, dataset);
                    break;
                case "salinvoice":
                    ParseImpSubledgerBlock(sectionLines, isReceivable: true, dataset, fallbackDate, defaultCurrency);
                    break;
                case "purinvoice":
                    ParseImpSubledgerBlock(sectionLines, isReceivable: false, dataset, fallbackDate, defaultCurrency);
                    break;
            }

            sectionLines.Clear();
        }

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var m = ImpSectionTagRegex().Match(trimmed);
            if (m.Success)
            {
                var tagName = m.Groups[1].Value;
                if (tagName.StartsWith('/'))
                {
                    FlushSection();
                    currentSection = null;
                }
                else
                {
                    FlushSection();
                    currentSection = tagName;
                    var remainder = m.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(remainder))
                        sectionLines.Add(remainder);
                }
                continue;
            }

            if (!string.IsNullOrEmpty(currentSection))
            {
                sectionLines.Add(line);
            }
        }

        FlushSection();
    }

    private static void ParseImpCoaSection(List<string> lines, ParsedMigrationDataset dataset)
    {
        var parsed = CsvReaderHelper.Parse(string.Join('\n', lines));
        foreach (var row in parsed)
        {
            if (row.Count < 2) continue;
            var code = row[0].Trim();
            var name = row[1].Trim();
            if (string.IsNullOrWhiteSpace(code) || code.Equals("Account", StringComparison.OrdinalIgnoreCase)) continue;

            var acctType = row.Count > 2 ? NormalizeAccountType(row[2], code) : InferTypeFromCode(code);
            var gifi = row.Count > 3 ? row[3].Trim() : string.Empty;
            decimal dr = 0m, cr = 0m;
            decimal? singleBal = null;

            if (row.Count >= 6 && TryParseMoney(row[4], out var parsedDr, false) && TryParseMoney(row[5], out var parsedCr, false))
            {
                dr = Math.Abs(parsedDr);
                cr = Math.Abs(parsedCr);
            }
            else if (row.Count >= 5 && TryParseMoney(row[4], out var parsedBal, false))
            {
                singleBal = parsedBal;
            }

            dataset.GlRows.Add(new ParsedGlAccountRow
            {
                Code = code,
                Name = name,
                AccountType = acctType,
                StandardTaxCode = gifi,
                Debit = dr,
                Credit = cr,
                HasExplicitDebitCredit = row.Count >= 6,
                RawSingleBalance = singleBal
            });
        }
    }

    private static void ParseImpPartySection(List<string> lines, bool isCustomer, ParsedMigrationDataset dataset)
    {
        var parsed = CsvReaderHelper.Parse(string.Join('\n', lines));
        foreach (var row in parsed)
        {
            if (row.Count == 0) continue;
            // Skip Sage header count line (single integer count)
            if (row.Count == 1 && int.TryParse(row[0].Trim(), out _)) continue;
            var name = row[0].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var contact = new PartyContactInfo
            {
                BusinessName = name,
                ContactPersonName = row.Count > 1 ? row[1].Trim() : string.Empty,
                BusinessAddressStreet = row.Count > 2 ? row[2].Trim() : string.Empty,
                BusinessAddressCity = row.Count > 4 ? row[4].Trim() : string.Empty,
                BusinessAddressState = row.Count > 5 ? row[5].Trim() : string.Empty,
                BusinessAddressCountry = row.Count > 6 ? row[6].Trim() : string.Empty,
                BusinessAddressZip = row.Count > 7 ? row[7].Trim() : string.Empty,
                ContactPersonPhone = row.Count > 8 ? row[8].Trim() : string.Empty,
                ContactPersonEmail = row.Count > 11 ? row[11].Trim() : string.Empty,
                TaxId = row.Count > 13 ? row[13].Trim() : string.Empty
            };

            var target = isCustomer ? dataset.Customers : dataset.Vendors;
            target.Add(new ParsedPartyRow
            {
                IsCustomer = isCustomer,
                ContactInfo = contact
            });
        }
    }

    private static void ParseImpSubledgerBlock(
        List<string> lines,
        bool isReceivable,
        ParsedMigrationDataset dataset,
        DateTime fallbackDate,
        string defaultCurrency)
    {
        // Standard Sage .imp transaction block:
        // Line 0: CustomerOrVendorName
        // Line 1: EntryCount, TransNo, InvNumber, Date (MM-DD-YYYY), ShipDate/DueDate, TotalAmount, Freight
        // Lines 2+: Item lines
        if (lines.Count < 2) return;

        var partyRows = CsvReaderHelper.Parse(lines[0]);
        var partyName = partyRows.FirstOrDefault()?.FirstOrDefault()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(partyName)) return;

        var headerRows = CsvReaderHelper.Parse(lines[1]);
        var hdr = headerRows.FirstOrDefault();
        if (hdr == null || hdr.Count < 5) return;

        var docNo = hdr.Count > 2 ? hdr[2].Trim() : string.Empty;
        var issueDate = TryParseDate(hdr.Count > 3 ? hdr[3] : null, fallbackDate);
        var amount = 0m;
        for (int i = hdr.Count - 1; i >= 4; i--)
        {
            if (TryParseMoney(hdr[i], out var val, false) && val != 0m)
            {
                // Pick main amount if last column is small freight/0
                amount = val;
                if (i <= 5) break;
            }
        }
        if (hdr.Count > 5 && TryParseMoney(hdr[5], out var col5, false) && col5 != 0m)
        {
            amount = col5;
        }

        if (amount == 0m) return;

        var item = new ParsedSubledgerRow
        {
            IsReceivable = isReceivable,
            DocumentNumber = docNo,
            PartyName = partyName,
            IssueDate = issueDate,
            DueDate = issueDate.AddDays(30),
            Currency = defaultCurrency,
            SubTotal = Math.Abs(amount),
            OpenAmount = amount,
            Notes = "Sage 50 .imp Open Item"
        };

        if (isReceivable) dataset.OpenInvoices.Add(item);
        else dataset.OpenBills.Add(item);
    }

    private static void ParseDelimitedFile(
        string text,
        string fileName,
        MigrationFileKind explicitKind,
        AccountingMigrationSourceFormat requestedFormat,
        ParsedMigrationDataset dataset,
        DateTime fallbackAsOfDate,
        string defaultCurrency)
    {
        var delimiter = DetectDelimiter(text);
        var useEuropeanComma = delimiter == ';';
        var allRows = CsvReaderHelper.Parse(text, delimiter)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();

        if (allRows.Count < 2)
        {
            if (allRows.Count == 1 && TryDetectSageExportBanner(allRows[0], out var emptyBannerKind) && emptyBannerKind == MigrationFileKind.Inventory)
            {
                dataset.InformationalNotices.Add($"Sage Inventory file '{fileName}' (0 items) skipped — Spokes does not track physical inventory.");
                return;
            }
            dataset.Warnings.Add($"Skipped '{fileName}': fewer than 2 non-empty rows.");
            return;
        }

        // Detect Sage 50 Export Records banner row ("33002,3,Accounts", "12001,1,...", etc.)
        if (TryDetectSageExportBanner(allRows[0], out var sageExportKind))
        {
            if (requestedFormat == AccountingMigrationSourceFormat.AutoDetect)
                dataset.DetectedFormat = AccountingMigrationSourceFormat.Sage50ExportCsv;

            var effectiveSageKind = explicitKind != MigrationFileKind.Auto ? explicitKind : sageExportKind;
            var sageRows = allRows.Skip(1).ToList();
            switch (effectiveSageKind)
            {
                case MigrationFileKind.Inventory:
                    dataset.InformationalNotices.Add($"Sage Inventory file '{fileName}' ({sageRows.Count} items) detected and skipped — Spokes does not track physical inventory.");
                    return;
                case MigrationFileKind.ChartOfAccounts:
                    ParseSageExportAccounts(sageRows, dataset);
                    return;
                case MigrationFileKind.Vendors:
                    ParseSageExportParties(sageRows, isCustomer: false, dataset, defaultCurrency);
                    return;
                case MigrationFileKind.Customers:
                    ParseSageExportParties(sageRows, isCustomer: true, dataset, defaultCurrency);
                    return;
                case MigrationFileKind.Employees:
                    ParseSageExportEmployees(sageRows, dataset);
                    return;
                case MigrationFileKind.Projects:
                    ParseSageExportProjects(sageRows, dataset);
                    return;
            }
        }

        if (explicitKind == MigrationFileKind.Inventory)
        {
            dataset.InformationalNotices.Add($"Inventory file '{fileName}' skipped — Spokes does not track physical inventory.");
            return;
        }

        if (explicitKind == MigrationFileKind.ClockifyTimesheets || (explicitKind == MigrationFileKind.Auto && IsClockifyCsv(Encoding.UTF8.GetBytes(text))))
        {
            dataset.InformationalNotices.Add($"Clockify detailed report '{fileName}' will be handled by the Clockify timesheet mapper.");
            return;
        }

        // Find actual column header row within top 12 lines
        var headerIdx = FindHeaderRowIndex(allRows);
        if (headerIdx < 0)
        {
            dataset.Warnings.Add($"Could not detect header row in '{fileName}'.");
            return;
        }

        var headers = allRows[headerIdx].Select(NormalizeHeader).ToList();

        if (requestedFormat == AccountingMigrationSourceFormat.AutoDetect &&
            headers.Any(h => h.Contains("nominal code") || h.Contains("ledger account")))
        {
            dataset.DetectedFormat = AccountingMigrationSourceFormat.SageBusinessCloudCsv;
        }

        var detectedKind = explicitKind != MigrationFileKind.Auto
            ? explicitKind
            : InferKindFromHeadersAndFileName(headers, fileName);

        var dataRows = allRows.Skip(headerIdx + 1).ToList();

        switch (detectedKind)
        {
            case MigrationFileKind.TrialBalance:
            case MigrationFileKind.ChartOfAccounts:
                ParseGlCsvRows(headers, dataRows, useEuropeanComma, dataset);
                break;
            case MigrationFileKind.Vendors:
                ParsePartyCsvRows(headers, dataRows, isCustomer: false, dataset);
                break;
            case MigrationFileKind.Customers:
                ParsePartyCsvRows(headers, dataRows, isCustomer: true, dataset);
                break;
            case MigrationFileKind.OpenReceivables:
                ParseSubledgerCsvRows(headers, dataRows, isReceivable: true, useEuropeanComma, fallbackAsOfDate, defaultCurrency, dataset);
                break;
            case MigrationFileKind.OpenPayables:
                ParseSubledgerCsvRows(headers, dataRows, isReceivable: false, useEuropeanComma, fallbackAsOfDate, defaultCurrency, dataset);
                break;
            case MigrationFileKind.PayrollYtd:
                ParsePayrollYtdCsvRows(headers, dataRows, useEuropeanComma, dataset);
                break;
            default:
                dataset.Warnings.Add($"Unable to determine data kind for '{fileName}'. Please tag the file type explicitly in the upload list.");
                break;
        }
    }

    private static void ParseGlCsvRows(
        List<string> headers,
        List<List<string>> rows,
        bool europeanComma,
        ParsedMigrationDataset dataset)
    {
        var codeCol = FindColumn(headers, "account number", "account no", "acct no", "acct #", "account code", "nominal code", "numero de compte", "no compte", "num compte", "code", "account");
        var nameCol = FindColumn(headers, "account name", "account description", "ledger account", "nom du compte", "libelle", "description", "name", "nom");
        var typeCol = FindColumn(headers, "account type", "type de compte", "class", "classe", "type", "category");
        var gifiCol = FindColumn(headers, "gifi", "igrf", "standard tax", "tax code", "code taxe");
        var drCol = FindColumn(headers, "debit", "debits", "solde debiteur", "ending debit");
        var crCol = FindColumn(headers, "credit", "credits", "solde crediteur", "ending credit");
        var balCol = FindColumn(headers, "balance", "ending balance", "net balance", "current balance", "solde", "solde final", "amount", "montant");

        if (codeCol < 0 && nameCol < 0) return;

        foreach (var r in rows)
        {
            var code = GetCell(r, codeCol);
            var name = GetCell(r, nameCol);

            // Handle combined "1020 - Operating Bank" or "1020 Operating Bank" when single column
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(code))
            {
                var split = SplitCombinedAccountLabel(code);
                code = split.Code;
                name = split.Name;
            }
            if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(name))
            {
                var split = SplitCombinedAccountLabel(name);
                if (!string.IsNullOrEmpty(split.Code))
                {
                    code = split.Code;
                    name = split.Name;
                }
            }

            if (string.IsNullOrWhiteSpace(code)) continue;
            if (code.Equals("total", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;

            var rawType = GetCell(r, typeCol);
            var normalizedType = NormalizeAccountType(rawType, code);
            var gifi = GetCell(r, gifiCol);

            decimal dr = 0m, cr = 0m;
            bool hasDrCr = false;
            decimal? singleBal = null;

            var drText = GetCell(r, drCol);
            var crText = GetCell(r, crCol);
            if (drCol >= 0 || crCol >= 0)
            {
                hasDrCr = true;
                TryParseMoney(drText, out dr, europeanComma);
                TryParseMoney(crText, out cr, europeanComma);
                if (dr < 0m) { cr += Math.Abs(dr); dr = 0m; }
                if (cr < 0m) { dr += Math.Abs(cr); cr = 0m; }
            }
            else if (balCol >= 0)
            {
                var balText = GetCell(r, balCol);
                if (TryParseMoney(balText, out var val, europeanComma))
                {
                    if (balText.TrimEnd().EndsWith("CR", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDrCr = true;
                        cr = Math.Abs(val);
                    }
                    else if (balText.TrimEnd().EndsWith("DR", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDrCr = true;
                        dr = Math.Abs(val);
                    }
                    else
                    {
                        singleBal = val;
                    }
                }
            }

            dataset.GlRows.Add(new ParsedGlAccountRow
            {
                Code = code,
                Name = string.IsNullOrWhiteSpace(name) ? code : name,
                AccountType = normalizedType,
                StandardTaxCode = gifi,
                Debit = Math.Round(dr, 2),
                Credit = Math.Round(cr, 2),
                HasExplicitDebitCredit = hasDrCr,
                RawSingleBalance = singleBal
            });
        }
    }

    private static void ParsePartyCsvRows(
        List<string> headers,
        List<List<string>> rows,
        bool isCustomer,
        ParsedMigrationDataset dataset)
    {
        var codeCol = FindColumn(headers, "vendor id", "supplier id", "customer id", "client id", "code", "id");
        var nameCol = FindColumn(headers, "business name", "company name", "vendor name", "supplier name", "customer name", "client name", "contact name", "vendor", "supplier", "customer", "client", "fournisseur", "company", "name", "nom");
        var contactCol = FindColumn(headers, "contact person", "contact", "attention", "personne ressource");
        var emailCol = FindColumn(headers, "email", "e mail", "courriel");
        var phoneCol = FindColumn(headers, "phone", "telephone", "tel", "phone 1");
        var streetCol = FindColumn(headers, "street", "address line 1", "address 1", "address", "adresse", "rue");
        var cityCol = FindColumn(headers, "city", "ville");
        var stateCol = FindColumn(headers, "province", "state", "region", "etat");
        var zipCol = FindColumn(headers, "postal code", "postal", "zip", "zip code", "code postal");
        var countryCol = FindColumn(headers, "country", "pays");
        var taxIdCol = FindColumn(headers, "tax id", "business number", "bn", "neq", "gst number", "tps", "vat");
        var defaultGlCol = FindColumn(headers, "default gl", "gl account", "expense account", "revenue account", "compte");

        if (nameCol < 0) return;

        foreach (var r in rows)
        {
            var bizName = GetCell(r, nameCol);
            if (string.IsNullOrWhiteSpace(bizName)) continue;
            if (bizName.Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;

            var contact = new PartyContactInfo
            {
                BusinessName = bizName,
                ContactPersonName = GetCell(r, contactCol),
                ContactPersonEmail = GetCell(r, emailCol),
                ContactPersonPhone = GetCell(r, phoneCol),
                BusinessAddressStreet = GetCell(r, streetCol),
                BusinessAddressCity = GetCell(r, cityCol),
                BusinessAddressState = GetCell(r, stateCol),
                BusinessAddressZip = GetCell(r, zipCol),
                BusinessAddressCountry = GetCell(r, countryCol),
                TaxId = GetCell(r, taxIdCol),
                DefaultGlAccountCode = GetCell(r, defaultGlCol)
            };

            var target = isCustomer ? dataset.Customers : dataset.Vendors;
            target.Add(new ParsedPartyRow
            {
                IsCustomer = isCustomer,
                SourceCode = GetCell(r, codeCol),
                ContactInfo = contact
            });
        }
    }

    private static void ParseSubledgerCsvRows(
        List<string> headers,
        List<List<string>> rows,
        bool isReceivable,
        bool europeanComma,
        DateTime fallbackDate,
        string defaultCurrency,
        ParsedMigrationDataset dataset)
    {
        var partyCol = FindColumn(headers, "customer name", "customer", "client", "vendor name", "vendor", "supplier", "fournisseur", "name", "nom");
        var docCol = FindColumn(headers, "invoice number", "invoice no", "invoice #", "inv #", "bill #", "bill number", "document", "reference", "ref", "trans no", "numero", "no facture");
        var dateCol = FindColumn(headers, "invoice date", "bill date", "trans date", "issue date", "date");
        var dueCol = FindColumn(headers, "due date", "date d echeance", "echeance");
        var openCol = FindColumn(headers, "balance due", "open balance", "amount due", "remaining", "solde du", "montant du", "total", "amount", "montant");
        var subCol = FindColumn(headers, "subtotal", "sub total", "sous total", "net");
        var taxCol = FindColumn(headers, "tax", "tps", "gst", "tvq", "qst", "total tax", "taxes");
        var currCol = FindColumn(headers, "currency", "devise");
        var memoCol = FindColumn(headers, "memo", "description", "notes");

        if (partyCol < 0 || openCol < 0) return;

        foreach (var r in rows)
        {
            var partyName = GetCell(r, partyCol);
            if (string.IsNullOrWhiteSpace(partyName) || partyName.StartsWith("Total", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseMoney(GetCell(r, openCol), out var openAmt, europeanComma) || openAmt == 0m) continue;

            var docNo = GetCell(r, docCol);
            var issue = TryParseDate(GetCell(r, dateCol), fallbackDate);
            var due = dueCol >= 0 ? TryParseDate(GetCell(r, dueCol), issue.AddDays(30)) : issue.AddDays(30);
            var sub = subCol >= 0 && TryParseMoney(GetCell(r, subCol), out var sVal, europeanComma) ? sVal : openAmt;
            var tax = taxCol >= 0 && TryParseMoney(GetCell(r, taxCol), out var tVal, europeanComma) ? tVal : 0m;
            var curr = GetCell(r, currCol);

            var entry = new ParsedSubledgerRow
            {
                IsReceivable = isReceivable,
                DocumentNumber = docNo,
                PartyName = partyName,
                IssueDate = issue,
                DueDate = due,
                Currency = string.IsNullOrWhiteSpace(curr) ? defaultCurrency : curr.ToUpperInvariant(),
                SubTotal = sub,
                TaxAmount = tax,
                OpenAmount = openAmt,
                Notes = GetCell(r, memoCol)
            };

            if (isReceivable) dataset.OpenInvoices.Add(entry);
            else dataset.OpenBills.Add(entry);
        }
    }

    private static void ParsePayrollYtdCsvRows(
        List<string> headers,
        List<List<string>> rows,
        bool europeanComma,
        ParsedMigrationDataset dataset)
    {
        var empCol = FindColumn(headers, "employee name", "employee", "employe", "full name", "name", "nom");
        var emailCol = FindColumn(headers, "email", "courriel");
        var grossCol = FindColumn(headers, "gross pay", "gross income", "gross", "salaire brut", "brut", "ytd gross");
        var vacCol = FindColumn(headers, "vacation accrued", "vacation", "vacances", "vacation pay");
        var fedTaxCol = FindColumn(headers, "federal tax", "fed tax", "impot federal", "cra tax", "income tax", "total tax");
        var provTaxCol = FindColumn(headers, "provincial tax", "quebec tax", "qc tax", "state tax", "impot provincial", "impot qc", "impot quebec");
        var qppEeCol = FindColumn(headers, "qpp", "rrq", "cpp", "qpp ee", "rrq ee", "cpp ee", "qpp employee");
        var qppErCol = FindColumn(headers, "qpp er", "rrq er", "cpp er", "qpp employer", "cpp employer");
        var qpp2EeCol = FindColumn(headers, "qpp2", "rrq2", "cpp2", "qpp 2");
        var qpp2ErCol = FindColumn(headers, "qpp2 er", "rrq2 er", "cpp2 er");
        var eiEeCol = FindColumn(headers, "ei", "ae", "ei ee", "ae ee", "ei employee");
        var eiErCol = FindColumn(headers, "ei er", "ae er", "ei employer");
        var qpipEeCol = FindColumn(headers, "qpip", "rqap", "qpip ee", "rqap ee");
        var qpipErCol = FindColumn(headers, "qpip er", "rqap er", "qpip employer");

        if (empCol < 0 || grossCol < 0) return;

        foreach (var r in rows)
        {
            var empName = GetCell(r, empCol);
            if (string.IsNullOrWhiteSpace(empName) || empName.StartsWith("Total", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseMoney(GetCell(r, grossCol), out var gross, europeanComma)) continue;

            decimal ReadM(int idx) => idx >= 0 && TryParseMoney(GetCell(r, idx), out var v, europeanComma) ? Math.Max(0m, v) : 0m;

            var qppEe = ReadM(qppEeCol);
            var qppEr = qppErCol >= 0 ? ReadM(qppErCol) : qppEe;
            var qpp2Ee = ReadM(qpp2EeCol);
            var qpp2Er = qpp2ErCol >= 0 ? ReadM(qpp2ErCol) : qpp2Ee;
            var eiEe = ReadM(eiEeCol);
            var eiEr = eiErCol >= 0 ? ReadM(eiErCol) : Math.Round(eiEe * 1.4m, 2);
            var qpipEe = ReadM(qpipEeCol);
            var qpipEr = qpipErCol >= 0 ? ReadM(qpipErCol) : Math.Round(qpipEe * 1.4008m, 2);

            dataset.PayrollYtds.Add(new ParsedPayrollYtdRow
            {
                EmployeeName = empName,
                Email = GetCell(r, emailCol),
                GrossPay = Math.Max(0m, gross),
                Vacation = ReadM(vacCol),
                FederalTax = ReadM(fedTaxCol),
                RegionalTax = ReadM(provTaxCol),
                QppCppEmployee = qppEe,
                QppCppEmployer = qppEr,
                Qpp2Employee = qpp2Ee,
                Qpp2Employer = qpp2Er,
                EiEmployee = eiEe,
                EiEmployer = eiEr,
                QpipEmployee = qpipEe,
                QpipEmployer = qpipEr
            });
        }
    }

    private static void ParseSageExportAccounts(List<List<string>> rows, ParsedMigrationDataset dataset)
    {
        // Format: Code(0), Name(1), StructureFlag(2: H/A/S/G/T/X), ClassLabel(3), Currency(4), GifiCode(5)
        foreach (var r in rows)
        {
            if (r.Count < 2) continue;
            var code = r[0].Trim();
            var name = r[1].Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            var structureFlag = r.Count > 2 ? r[2].Trim().ToUpperInvariant() : "A";
            // Only import postable detail/group accounts ("A" and "G"). Skip Headings ("H"), Subtotals ("S"), Totals ("T"), and Current Earnings ("X").
            if (structureFlag is "H" or "S" or "T" or "X") continue;

            var classLabel = r.Count > 3 ? r[3].Trim() : string.Empty;
            var gifi = r.Count > 5 ? r[5].Trim() : string.Empty;

            var (acctType, subType) = MapSageClassToTypeAndSubType(classLabel, code, name);
            var isObsolete = name.Contains("xxxxx", StringComparison.OrdinalIgnoreCase) ||
                             Regex.IsMatch(name, @"\b(199\d|200\d|201[0-6])\b") && classLabel.Contains("Immobilis", StringComparison.OrdinalIgnoreCase);

            dataset.GlRows.Add(new ParsedGlAccountRow
            {
                Code = code,
                Name = name,
                AccountType = acctType,
                SubType = subType,
                ClassLabel = classLabel,
                StandardTaxCode = gifi,
                IsLikelyObsolete = isObsolete
            });
        }
    }

    private static (string Type, string SubType) MapSageClassToTypeAndSubType(string classLabel, string code, string name)
    {
        var c = PartyMatchingHelper.StripDiacritics($"{classLabel} {name}").ToLowerInvariant();

        if (c.Contains("encaisse") || c.Contains("banque") || c.Contains("liquidites") || c.Contains("bank") || c.Contains("cash"))
            return (GlAccountType.Asset, GlAccountSubType.Bank);
        if (c.Contains("comptes clients") || c.Contains("clients a recevoir") || c.Contains("accounts receivable"))
            return (GlAccountType.Asset, GlAccountSubType.AccountsReceivable);
        if (c.Contains("amortissement cumule") || c.Contains("accumulated depreciation") || c.Contains("immobilisations") || c.Contains("fixed asset"))
            return (GlAccountType.Asset, GlAccountSubType.FixedAsset);
        if (c.Contains("actif a court terme") || c.Contains("current asset") || c.Contains("frais payes d avance") || c.Contains("stock") || c.Contains("inventaire") || c.Contains("depots") || c.Contains("prets et avances"))
            return (GlAccountType.Asset, GlAccountSubType.PrepaidOrOtherAsset);

        if (c.Contains("carte de credit") || c.Contains("credit card") || c.Contains("visa") || c.Contains("mastercard") || c.Contains("amex"))
            return (GlAccountType.Liability, GlAccountSubType.CreditCard);
        if (c.Contains("comptes fournisseurs") || c.Contains("accounts payable"))
            return (GlAccountType.Liability, GlAccountSubType.AccountsPayable);
        if (c.Contains("tps") || c.Contains("tvq") || c.Contains("gst") || c.Contains("qst") || c.Contains("taxe de vente"))
            return (GlAccountType.Liability, GlAccountSubType.SalesTax);
        if (c.Contains("das") || c.Contains("retenues") || c.Contains("salaires a payer") || c.Contains("vacances") || c.Contains("cnesst") || c.Contains("csst") || c.Contains("rrq") || c.Contains("rqap"))
            return (GlAccountType.Liability, GlAccountSubType.PayrollLiability);
        if (c.Contains("passif a long terme") || c.Contains("passif a court terme") || c.Contains("passif") || c.Contains("liability"))
            return (GlAccountType.Liability, GlAccountSubType.OtherLiability);

        if (c.Contains("capital-actions") || c.Contains("capital actions") || c.Contains("benefices non repartis") || c.Contains("avoir") || c.Contains("equity"))
            return (GlAccountType.Equity, GlAccountSubType.Equity);

        if (c.Contains("autres produits") || c.Contains("other income") || c.Contains("interet"))
            return (GlAccountType.Revenue, GlAccountSubType.OtherIncome);
        if (c.Contains("produits d'exploitation") || c.Contains("produit") || c.Contains("revenu") || c.Contains("vente") || c.Contains("revenue"))
            return (GlAccountType.Revenue, GlAccountSubType.OperatingRevenue);

        if (c.Contains("cout des marchandises") || c.Contains("achats") || c.Contains("sous-trait") || c.Contains("cost of goods"))
            return (GlAccountType.Expense, GlAccountSubType.DirectCost);
        if (c.Contains("charges salariales") || c.Contains("salaires") || c.Contains("payroll") || c.Contains("avantages sociaux"))
            return (GlAccountType.Expense, GlAccountSubType.PayrollExpense);
        if (c.Contains("frais generaux") || c.Contains("exploitation") || c.Contains("charge") || c.Contains("depense") || c.Contains("expense") || c.Contains("amortissement"))
            return (GlAccountType.Expense, GlAccountSubType.Overhead);

        var inferredType = InferTypeFromCode(code);
        return (inferredType, GlAccountSubType.GetDefaultForAccountType(inferredType));
    }

    private static void ParseSageExportParties(
        List<List<string>> rows,
        bool isCustomer,
        ParsedMigrationDataset dataset,
        string defaultCurrency)
    {
        // Format: Name(0), Contact(1), Street1(2), Street2(3), City(4), Province(5), Country(6), Postal(7), Phone1(8), Phone2(9), Fax(10), Email(11), Website(12), Currency(13), TermsOrTax(14)
        foreach (var r in rows)
        {
            if (r.Count == 0) continue;
            var name = r[0].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var street1 = r.Count > 2 ? r[2].Trim() : string.Empty;
            var street2 = r.Count > 3 ? r[3].Trim() : string.Empty;
            var street = string.IsNullOrWhiteSpace(street2) ? street1 : $"{street1}, {street2}".Trim(',', ' ');

            var rawCurrency = r.Count > 13 ? r[13].Trim().ToUpperInvariant() : string.Empty;
            var currency = rawCurrency is "CAD" or "USD" or "EUR" or "GBP" ? rawCurrency : defaultCurrency;

            var col14 = r.Count > 14 ? r[14].Trim() : string.Empty;
            var taxId = string.Empty;
            if (!string.IsNullOrWhiteSpace(col14) &&
                !col14.Equals("Courant", StringComparison.OrdinalIgnoreCase) &&
                !col14.StartsWith("Net", StringComparison.OrdinalIgnoreCase) &&
                col14.Any(char.IsDigit))
            {
                taxId = col14;
            }

            var contact = new PartyContactInfo
            {
                BusinessName = name,
                ContactPersonName = r.Count > 1 ? r[1].Trim() : string.Empty,
                BusinessAddressStreet = street,
                BusinessAddressCity = r.Count > 4 ? r[4].Trim() : string.Empty,
                BusinessAddressState = r.Count > 5 ? r[5].Trim() : string.Empty,
                BusinessAddressCountry = r.Count > 6 ? r[6].Trim() : string.Empty,
                BusinessAddressZip = r.Count > 7 ? r[7].Trim() : string.Empty,
                ContactPersonPhone = r.Count > 8 && !string.IsNullOrWhiteSpace(r[8]) ? r[8].Trim() : (r.Count > 9 ? r[9].Trim() : string.Empty),
                ContactPersonEmail = r.Count > 11 ? r[11].Trim() : string.Empty,
                TaxId = taxId
            };

            var target = isCustomer ? dataset.Customers : dataset.Vendors;
            target.Add(new ParsedPartyRow
            {
                IsCustomer = isCustomer,
                Website = r.Count > 12 ? r[12].Trim() : string.Empty,
                DefaultCurrency = currency,
                ContactInfo = contact
            });
        }
    }

    private static void ParseSageExportEmployees(List<List<string>> rows, ParsedMigrationDataset dataset)
    {
        // Format: Name(0), Street1(1), Street2(2), City(3), Province(4), Postal(5), Phone1(6), Phone2(7), SIN(8), BirthDate(9), HireDate(10), TaxProv(11), PayPeriods(12)
        foreach (var r in rows)
        {
            if (r.Count == 0) continue;
            var rawName = r[0].Trim();
            if (string.IsNullOrWhiteSpace(rawName)) continue;

            var (firstName, lastName) = SplitPersonName(rawName);
            var rawSin = r.Count > 8 ? new string(r[8].Where(char.IsDigit).ToArray()) : string.Empty;
            if (string.IsNullOrEmpty(rawSin) || rawSin.All(ch => ch == '0'))
            {
                rawSin = string.Empty;
            }

            var birth = r.Count > 9 ? TryParseOptionalDateOnly(r[9]) : null;
            var hire = r.Count > 10 ? TryParseOptionalDateOnly(r[10]) : null;
            var periods = r.Count > 12 && int.TryParse(r[12].Trim(), out var p) && p > 0 ? p : 26;

            dataset.PayrollYtds.Add(new ParsedPayrollYtdRow
            {
                EmployeeName = rawName,
                FirstName = firstName,
                LastName = lastName,
                Street1 = r.Count > 1 ? r[1].Trim() : string.Empty,
                Street2 = r.Count > 2 ? r[2].Trim() : string.Empty,
                City = r.Count > 3 ? r[3].Trim() : string.Empty,
                Province = r.Count > 4 ? r[4].Trim() : string.Empty,
                PostalCode = r.Count > 5 ? r[5].Trim() : string.Empty,
                PhoneNumber = r.Count > 6 ? r[6].Trim() : string.Empty,
                SecondaryPhone = r.Count > 7 ? r[7].Trim() : string.Empty,
                SocialInsuranceNumber = rawSin,
                BirthDate = birth,
                HireDate = hire,
                PayPeriodsPerYear = periods,
                FromEmployeeDirectoryFile = true
            });
        }
    }

    private static void ParseSageExportProjects(List<List<string>> rows, ParsedMigrationDataset dataset)
    {
        // Format: RawName(0), StartDate(1)
        foreach (var r in rows)
        {
            if (r.Count == 0) continue;
            var rawName = r[0].Trim();
            if (string.IsNullOrWhiteSpace(rawName)) continue;

            DateTime? startDate = r.Count > 1 && TryParseExactDate(r[1], out var parsedDate) ? parsedDate : null;
            var (code, cleanName) = ExtractProjectCodeAndName(rawName);
            var isInternal = IsInternalProjectBucket(rawName);

            dataset.Projects.Add(new ParsedProjectRow
            {
                RawName = rawName,
                ExtractedCode = code,
                CleanName = string.IsNullOrWhiteSpace(cleanName) ? rawName : cleanName,
                StartDate = startDate,
                IsInternalBucket = isInternal
            });
        }
    }

    private static (string FirstName, string LastName) SplitPersonName(string rawName)
    {
        var trimmed = rawName.Trim();
        if (trimmed.Contains(','))
        {
            var commaParts = trimmed.Split(',', 2, StringSplitOptions.TrimEntries);
            return (commaParts[1], commaParts[0]);
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return (parts[0], string.Empty);
        if (parts.Length == 2) return (parts[0], parts[1]);
        return (string.Join(' ', parts[..^1]), parts[^1]);
    }

    private static (string Code, string Name) ExtractProjectCodeAndName(string rawName)
    {
        var m = Regex.Match(rawName.Trim(), @"^([A-Za-z]{2,5}[\s-]?\d{2,4}[A-Za-z]?)\s*[-–:\s]*\s*(.*)$");
        if (m.Success)
        {
            var code = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
            var rest = m.Groups[2].Value.Trim(' ', '-', '–', ':');
            return (code, string.IsNullOrWhiteSpace(rest) ? rawName.Trim() : rest);
        }

        return (string.Empty, rawName.Trim());
    }

    private static bool IsInternalProjectBucket(string rawName)
    {
        var clean = PartyMatchingHelper.StripDiacritics(rawName).ToLowerInvariant().Trim();
        return clean.StartsWith("admin", StringComparison.Ordinal) ||
               clean.Contains("vacance", StringComparison.Ordinal) ||
               clean.Contains("atelier", StringComparison.Ordinal) ||
               clean.Contains("party bureau", StringComparison.Ordinal) ||
               clean.Equals("avance", StringComparison.Ordinal) ||
               clean.StartsWith("avance ", StringComparison.Ordinal) ||
               clean.Equals("transport", StringComparison.Ordinal);
    }

    private static DateOnly? TryParseOptionalDateOnly(string? raw)
    {
        if (TryParseExactDate(raw, out var dt))
            return DateOnly.FromDateTime(dt);
        return null;
    }

    private static bool TryParseExactDate(string? raw, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var clean = raw.Trim();
        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "MM-dd-yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "d/M/yyyy"];
        if (DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return true;
        return DateTime.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private static void DeduplicateAndMergeEmployeeRows(ParsedMigrationDataset dataset)
    {
        if (dataset.PayrollYtds.Count <= 1) return;

        var merged = new List<ParsedPayrollYtdRow>();
        foreach (var row in dataset.PayrollYtds)
        {
            var tokenKey = PartyMatchingHelper.NormalizeTokenSortedName(row.EmployeeName);
            var existing = merged.FirstOrDefault(e =>
                (!string.IsNullOrEmpty(row.SocialInsuranceNumber) && string.Equals(e.SocialInsuranceNumber, row.SocialInsuranceNumber, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(tokenKey) && PartyMatchingHelper.NormalizeTokenSortedName(e.EmployeeName) == tokenKey));

            if (existing == null)
            {
                merged.Add(row);
                continue;
            }

            // Merge directory fields and YTD numbers into the single deduplicated record
            if (existing.HireDate == null || (row.HireDate != null && row.HireDate > existing.HireDate))
            {
                existing.DuplicateMergedFromName = existing.EmployeeName;
                existing.EmployeeName = row.EmployeeName;
                existing.FirstName = row.FirstName;
                existing.LastName = row.LastName;
                existing.HireDate = row.HireDate;
            }
            else if (string.IsNullOrEmpty(existing.DuplicateMergedFromName))
            {
                existing.DuplicateMergedFromName = row.EmployeeName;
            }

            if (!string.IsNullOrEmpty(row.SocialInsuranceNumber) && string.Equals(existing.SocialInsuranceNumber, row.SocialInsuranceNumber, StringComparison.OrdinalIgnoreCase))
            {
                existing.HasDuplicateSinWarning = true;
            }

            if (string.IsNullOrWhiteSpace(existing.SocialInsuranceNumber)) existing.SocialInsuranceNumber = row.SocialInsuranceNumber;
            if (string.IsNullOrWhiteSpace(existing.Email)) existing.Email = row.Email;
            if (string.IsNullOrWhiteSpace(existing.Street1)) existing.Street1 = row.Street1;
            if (string.IsNullOrWhiteSpace(existing.Street2)) existing.Street2 = row.Street2;
            if (string.IsNullOrWhiteSpace(existing.City)) existing.City = row.City;
            if (string.IsNullOrWhiteSpace(existing.Province)) existing.Province = row.Province;
            if (string.IsNullOrWhiteSpace(existing.PostalCode)) existing.PostalCode = row.PostalCode;
            if (string.IsNullOrWhiteSpace(existing.PhoneNumber)) existing.PhoneNumber = row.PhoneNumber;
            if (existing.BirthDate == null) existing.BirthDate = row.BirthDate;
            if (row.FromEmployeeDirectoryFile) existing.FromEmployeeDirectoryFile = true;

            if (row.GrossPay > 0m)
            {
                existing.GrossPay += row.GrossPay;
                existing.Vacation += row.Vacation;
                existing.FederalTax += row.FederalTax;
                existing.RegionalTax += row.RegionalTax;
                existing.QppCppEmployee += row.QppCppEmployee;
                existing.QppCppEmployer += row.QppCppEmployer;
                existing.Qpp2Employee += row.Qpp2Employee;
                existing.Qpp2Employer += row.Qpp2Employer;
                existing.EiEmployee += row.EiEmployee;
                existing.EiEmployer += row.EiEmployer;
                existing.QpipEmployee += row.QpipEmployee;
                existing.QpipEmployer += row.QpipEmployer;
            }
        }

        dataset.PayrollYtds = merged;
    }

    private static void FinalizeSingleColumnTrialBalance(List<ParsedGlAccountRow> rows)
    {
        var singleColRows = rows.Where(r => !r.HasExplicitDebitCredit && r.RawSingleBalance.HasValue).ToList();
        if (singleColRows.Count == 0) return;

        var rawSum = singleColRows.Sum(r => r.RawSingleBalance!.Value);
        var hasNegatives = singleColRows.Any(r => r.RawSingleBalance!.Value < 0m);
        // If signed convention (+Dr / -Cr), the sum across all accounts is ~0.
        var isSignedConvention = hasNegatives && Math.Abs(rawSum) <= 0.05m;

        foreach (var row in singleColRows)
        {
            var val = row.RawSingleBalance!.Value;
            if (isSignedConvention)
            {
                if (val >= 0m) { row.Debit = Math.Round(val, 2); row.Credit = 0m; }
                else { row.Debit = 0m; row.Credit = Math.Round(Math.Abs(val), 2); }
            }
            else
            {
                var isDebitNormal = row.AccountType == GlAccountType.Asset || row.AccountType == GlAccountType.Expense;
                if (isDebitNormal)
                {
                    if (val >= 0m) row.Debit = Math.Round(val, 2);
                    else row.Credit = Math.Round(Math.Abs(val), 2);
                }
                else
                {
                    if (val >= 0m) row.Credit = Math.Round(val, 2);
                    else row.Debit = Math.Round(Math.Abs(val), 2);
                }
            }
        }
    }

    private static void DeduplicateGlRows(ParsedMigrationDataset dataset)
    {
        if (dataset.GlRows.Count <= 1) return;
        var merged = new Dictionary<string, ParsedGlAccountRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dataset.GlRows)
        {
            if (merged.TryGetValue(row.Code, out var existing))
            {
                existing.Debit = Math.Round(existing.Debit + row.Debit, 2);
                existing.Credit = Math.Round(existing.Credit + row.Credit, 2);
                if (string.IsNullOrWhiteSpace(existing.StandardTaxCode) && !string.IsNullOrWhiteSpace(row.StandardTaxCode))
                    existing.StandardTaxCode = row.StandardTaxCode;
                if (string.IsNullOrWhiteSpace(existing.SubType) && !string.IsNullOrWhiteSpace(row.SubType))
                    existing.SubType = row.SubType;
                if (string.IsNullOrWhiteSpace(existing.ClassLabel) && !string.IsNullOrWhiteSpace(row.ClassLabel))
                    existing.ClassLabel = row.ClassLabel;
                if (row.IsLikelyObsolete)
                    existing.IsLikelyObsolete = true;
            }
            else
            {
                merged[row.Code] = row;
            }
        }
        dataset.GlRows = merged.Values.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static char DetectDelimiter(string text)
    {
        var sampleLines = text.Split('\n').Take(8);
        int commas = 0, semis = 0, tabs = 0;
        foreach (var l in sampleLines)
        {
            commas += l.Count(c => c == ',');
            semis += l.Count(c => c == ';');
            tabs += l.Count(c => c == '\t');
        }
        if (tabs > commas && tabs > semis) return '\t';
        if (semis > commas) return ';';
        return ',';
    }

    private static int FindHeaderRowIndex(List<List<string>> rows)
    {
        var limit = Math.Min(rows.Count, 12);
        int bestIdx = 0;
        int bestScore = -1;

        for (int i = 0; i < limit; i++)
        {
            var r = rows[i];
            if (r.Count == 1 && r[0].Trim() == "12001") continue;
            if (r.Count(c => !string.IsNullOrWhiteSpace(c)) < 2) continue;

            var norm = r.Select(NormalizeHeader).ToList();
            int score = 0;
            foreach (var cell in norm)
            {
                if (cell is "debit" or "credit" or "balance" or "solde" or "account" or "code" or "name" or "nom"
                    or "email" or "courriel" or "customer" or "client" or "vendor" or "supplier" or "fournisseur"
                    or "employee" or "employe" or "gross" or "gross pay" or "amount" or "total" or "date" or "invoice number")
                {
                    score += 3;
                }
                else if (cell.Contains("account") || cell.Contains("compte") || cell.Contains("balance") || cell.Contains("invoice") || cell.Contains("vendor") || cell.Contains("customer") || cell.Contains("employee") || cell.Contains("tax"))
                {
                    score += 2;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static MigrationFileKind InferKindFromHeadersAndFileName(List<string> headers, string fileName)
    {
        var lowerName = PartyMatchingHelper.StripDiacritics(fileName).ToLowerInvariant();
        var joined = string.Join('|', headers);

        if (joined.Contains("gross") || joined.Contains("brut") || joined.Contains("qpp") || joined.Contains("rrq") || joined.Contains("cpp") || lowerName.Contains("payroll") || lowerName.Contains("paie") || lowerName.Contains("ytd"))
            return MigrationFileKind.PayrollYtd;

        if ((joined.Contains("debit") || joined.Contains("credit") || joined.Contains("solde") || joined.Contains("balance")) &&
            (joined.Contains("account") || joined.Contains("compte") || joined.Contains("nominal") || joined.Contains("code")))
            return MigrationFileKind.TrialBalance;

        if ((joined.Contains("invoice") || joined.Contains("bill") || joined.Contains("facture") || joined.Contains("due date") || joined.Contains("echeance")) &&
            (joined.Contains("balance") || joined.Contains("amount") || joined.Contains("total") || joined.Contains("montant")))
        {
            if (joined.Contains("vendor") || joined.Contains("supplier") || joined.Contains("fournisseur") || lowerName.Contains("ap") || lowerName.Contains("payable") || lowerName.Contains("bill") || lowerName.Contains("fournisseur"))
                return MigrationFileKind.OpenPayables;
            return MigrationFileKind.OpenReceivables;
        }

        if (joined.Contains("vendor") || joined.Contains("supplier") || joined.Contains("fournisseur") || lowerName.Contains("vendor") || lowerName.Contains("supplier") || lowerName.Contains("fournisseur"))
            return MigrationFileKind.Vendors;

        if (joined.Contains("customer") || joined.Contains("client") || lowerName.Contains("customer") || lowerName.Contains("client"))
            return MigrationFileKind.Customers;

        if (joined.Contains("account") || joined.Contains("compte") || joined.Contains("gifi"))
            return MigrationFileKind.ChartOfAccounts;

        return MigrationFileKind.Auto;
    }

    private static string NormalizeHeader(string header)
    {
        var clean = PartyMatchingHelper.StripDiacritics(header).ToLowerInvariant();
        clean = clean.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();
        return Regex.Replace(clean, @"\s+", " ");
    }

    private static int FindColumn(List<string> headers, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            var idx = headers.FindIndex(h => string.Equals(h, alias, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;
        }
        foreach (var alias in aliases)
        {
            if (alias.Length < 4) continue;
            var idx = headers.FindIndex(h => h.Contains(alias, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static string GetCell(List<string> row, int col) =>
        col >= 0 && col < row.Count ? row[col].Trim() : string.Empty;

    private static (string Code, string Name) SplitCombinedAccountLabel(string raw)
    {
        var trimmed = raw.Trim();
        var m = Regex.Match(trimmed, @"^([A-Za-z0-9]{3,10})\s*[-–:\s]\s*(.+)$");
        if (m.Success)
        {
            return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        }
        return (trimmed, string.Empty);
    }

    public static bool TryParseMoney(string? raw, out decimal result, bool preferEuropeanComma)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim().Replace("\u00A0", "").Replace("\u202F", "");
        var isNeg = false;
        var paren = ParenNegativeRegex().Match(s);
        if (paren.Success)
        {
            isNeg = true;
            s = paren.Groups[1].Value.Trim();
        }
        if (s.EndsWith("CR", StringComparison.OrdinalIgnoreCase) || s.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2].Trim();
        }

        s = s.Replace("$", "").Replace("€", "").Replace("CAD", "", StringComparison.OrdinalIgnoreCase).Replace("USD", "", StringComparison.OrdinalIgnoreCase).Trim();
        s = s.Replace(" ", "");
        if (string.IsNullOrEmpty(s)) return false;

        // Smart decimal separator normalization
        if (preferEuropeanComma && s.Contains(',') && !s.Contains('.'))
        {
            s = s.Replace(',', '.');
        }
        else if (s.Contains(',') && s.Contains('.'))
        {
            // "1,234.56" vs "1.234,56"
            if (s.LastIndexOf(',') > s.LastIndexOf('.'))
                s = s.Replace(".", "").Replace(',', '.');
            else
                s = s.Replace(",", "");
        }
        else if (s.Contains(','))
        {
            var afterComma = s.Length - s.LastIndexOf(',') - 1;
            s = afterComma == 2 ? s.Replace(',', '.') : s.Replace(",", "");
        }

        if (!decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            return false;

        result = isNeg ? -Math.Abs(parsed) : parsed;
        return true;
    }

    private static DateTime TryParseDate(string? raw, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var clean = raw.Trim();
        string[] formats = ["yyyy-MM-dd", "MM-dd-yyyy", "MM/dd/yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact.Date;
        if (DateTime.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.Date;
        return fallback;
    }

    private static string NormalizeAccountType(string raw, string code)
    {
        var clean = PartyMatchingHelper.StripDiacritics(raw).ToLowerInvariant();
        if (clean.Contains("asset") || clean.Contains("actif") || clean == "a") return GlAccountType.Asset;
        if (clean.Contains("liab") || clean.Contains("passif") || clean == "l") return GlAccountType.Liability;
        if (clean.Contains("equit") || clean.Contains("avoir") || clean.Contains("capital") || clean == "q") return GlAccountType.Equity;
        if (clean.Contains("rev") || clean.Contains("income") || clean.Contains("produit") || clean.Contains("vente") || clean == "r") return GlAccountType.Revenue;
        if (clean.Contains("exp") || clean.Contains("cost") || clean.Contains("charge") || clean.Contains("depense") || clean == "e" || clean == "x") return GlAccountType.Expense;
        return InferTypeFromCode(code);
    }

    private static string InferTypeFromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return GlAccountType.Expense;
        return code[0] switch
        {
            '1' => GlAccountType.Asset,
            '2' => GlAccountType.Liability,
            '3' => GlAccountType.Equity,
            '4' => GlAccountType.Revenue,
            _ => GlAccountType.Expense
        };
    }
}
