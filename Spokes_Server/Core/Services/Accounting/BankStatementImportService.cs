namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;

public class CsvBankImportMapping
{
    public bool HasHeaderRow { get; set; } = true;
    public int DateColumnIndex { get; set; } = 0;
    public int DescriptionColumnIndex { get; set; } = 1;
    public int? PayeeColumnIndex { get; set; }
    public int? AmountColumnIndex { get; set; } = 2; // Positive = deposit, Negative = withdrawal
    public int? DebitWithdrawalColumnIndex { get; set; }
    public int? CreditDepositColumnIndex { get; set; }
    public int? ReferenceColumnIndex { get; set; }
    public int? BalanceColumnIndex { get; set; }
}

/// <summary>
/// Universal Bank & Credit Card Statement Parser supporting:
/// 1. OFX 1.x (SGML) & OFX 2.x / QFX (XML) (Desjardins, RBC, TD, BMO, National Bank, Scotiabank, Chase, etc.)
/// 2. ISO 20022 CAMT.053 XML
/// 3. Configurable or Auto-Detected CSV
/// </summary>
public class BankStatementImportService
{
    private static readonly Regex OfxStmtTrnRegex = new(
        @"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public BankStatement ParseStatement(
        string content,
        string fileName,
        string glAccountCode = "1020",
        CsvBankImportMapping? csvMapping = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Statement content is empty.", nameof(content));
        }

        var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var ext = System.IO.Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        if (ext is ".ofx" or ".qfx" ||
            trimmed.Contains("<OFX", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("OFXHEADER:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<STMTTRN>", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOfxOrQfx(trimmed, fileName ?? "statement.ofx", glAccountCode);
        }

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<BkToCstmrStmt", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("camt.053", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCamt053Xml(trimmed, fileName ?? "statement.xml", glAccountCode);
        }

        return ParseCsv(trimmed, fileName ?? "statement.csv", glAccountCode, csvMapping);
    }

    public BankStatement ParseOfxOrQfx(string content, string fileName, string glAccountCode = "1020")
    {
        var statement = new BankStatement
        {
            GlAccountCode = glAccountCode,
            SourceFileName = fileName,
            SourceFormat = fileName.EndsWith(".qfx", StringComparison.OrdinalIgnoreCase) ? "QFX" : "OFX",
            StatementName = System.IO.Path.GetFileNameWithoutExtension(fileName)
        };

        var curDef = ExtractOfxTagValue(content, "CURDEF");
        if (!string.IsNullOrWhiteSpace(curDef))
        {
            statement.Currency = curDef.Trim().ToUpperInvariant();
        }

        var dtStart = ParseOfxDate(ExtractOfxTagValue(content, "DTSTART"));
        var dtEnd = ParseOfxDate(ExtractOfxTagValue(content, "DTEND"));

        var ledgerBalBlock = ExtractOfxSection(content, "LEDGERBAL");
        if (!string.IsNullOrWhiteSpace(ledgerBalBlock))
        {
            var balAmtStr = ExtractOfxTagValue(ledgerBalBlock, "BALAMT");
            if (TryParseDecimal(balAmtStr, out var closingBal))
            {
                statement.ClosingBalance = closingBal;
            }
        }

        var matches = OfxStmtTrnRegex.Matches(content);
        foreach (Match match in matches)
        {
            var block = match.Groups[1].Value;
            var amtStr = ExtractOfxTagValue(block, "TRNAMT");
            if (!TryParseDecimal(amtStr, out var amount))
                continue;

            var trnType = ExtractOfxTagValue(block, "TRNTYPE") ?? string.Empty;
            var dtPosted = ParseOfxDate(ExtractOfxTagValue(block, "DTPOSTED")) ?? DateTime.Today;
            var fitId = ExtractOfxTagValue(block, "FITID") ?? string.Empty;
            var name = DecodeSgmlEntities(ExtractOfxTagValue(block, "NAME") ?? string.Empty);
            var memo = DecodeSgmlEntities(ExtractOfxTagValue(block, "MEMO") ?? string.Empty);
            var checkNum = ExtractOfxTagValue(block, "CHECKNUM") ?? ExtractOfxTagValue(block, "REFNUM") ?? string.Empty;

            if (string.Equals(trnType, "DEBIT", StringComparison.OrdinalIgnoreCase) && amount > 0m)
            {
                amount = -amount;
            }

            var description = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(memo) && !memo.Contains(name, StringComparison.OrdinalIgnoreCase)
                ? $"{name} — {memo}"
                : (!string.IsNullOrWhiteSpace(name) ? name : memo);

            statement.Lines.Add(new BankTransactionLine
            {
                Date = dtPosted,
                Payee = !string.IsNullOrWhiteSpace(name) ? name : memo,
                Description = description,
                Amount = Math.Round(amount, 2),
                ReferenceNumber = checkNum,
                ExternalFitId = fitId
            });
        }

        FinalizeStatementDatesAndBalances(statement, dtStart, dtEnd);
        return statement;
    }

    public BankStatement ParseCamt053Xml(string xmlContent, string fileName, string glAccountCode = "1020")
    {
        var doc = XDocument.Parse(xmlContent);
        var statement = new BankStatement
        {
            GlAccountCode = glAccountCode,
            SourceFileName = fileName,
            SourceFormat = "CAMT053",
            StatementName = System.IO.Path.GetFileNameWithoutExtension(fileName)
        };

        // Parse balances (<Bal>)
        foreach (var balEl in doc.Descendants().Where(e => e.Name.LocalName == "Bal"))
        {
            var code = balEl.Descendants().FirstOrDefault(e => e.Name.LocalName is "Cd" or "Prtry")?.Value ?? string.Empty;
            var amtEl = balEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "Amt");
            var cdtDbt = balEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "CdtDbtInd")?.Value ?? "CRDT";

            if (amtEl != null && TryParseDecimal(amtEl.Value, out var balVal))
            {
                var ccyAttr = amtEl.Attribute("Ccy")?.Value;
                if (!string.IsNullOrWhiteSpace(ccyAttr))
                {
                    statement.Currency = ccyAttr.ToUpperInvariant();
                }

                if (string.Equals(cdtDbt, "DBIT", StringComparison.OrdinalIgnoreCase))
                {
                    balVal = -balVal;
                }

                if (string.Equals(code, "OPBD", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PRCD", StringComparison.OrdinalIgnoreCase))
                {
                    statement.OpeningBalance = balVal;
                }
                else if (string.Equals(code, "CLBD", StringComparison.OrdinalIgnoreCase))
                {
                    statement.ClosingBalance = balVal;
                }
            }
        }

        // Parse entries (<Ntry>)
        foreach (var ntry in doc.Descendants().Where(e => e.Name.LocalName == "Ntry"))
        {
            var amtEl = ntry.Elements().FirstOrDefault(e => e.Name.LocalName == "Amt");
            if (amtEl == null || !TryParseDecimal(amtEl.Value, out var amt))
                continue;

            var cdtDbt = ntry.Elements().FirstOrDefault(e => e.Name.LocalName == "CdtDbtInd")?.Value ?? "CRDT";
            if (string.Equals(cdtDbt, "DBIT", StringComparison.OrdinalIgnoreCase) && amt > 0m)
            {
                amt = -amt;
            }

            var dateStr = ntry.Descendants().FirstOrDefault(e => e.Name.LocalName is "Dt" or "DtTm")?.Value;
            var date = TryParseFlexibleDate(dateStr, out var d) ? d : DateTime.Today;
            var refId = ntry.Elements().FirstOrDefault(e => e.Name.LocalName == "AcctSvcrRef")?.Value
                        ?? ntry.Descendants().FirstOrDefault(e => e.Name.LocalName == "EndToEndId")?.Value
                        ?? string.Empty;
            var rmtInfo = string.Join(" ", ntry.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()));
            var partyName = ntry.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nm")?.Value ?? string.Empty;

            statement.Lines.Add(new BankTransactionLine
            {
                Date = date,
                Payee = !string.IsNullOrWhiteSpace(partyName) ? partyName : rmtInfo,
                Description = !string.IsNullOrWhiteSpace(rmtInfo) ? rmtInfo : partyName,
                Amount = Math.Round(amt, 2),
                ReferenceNumber = refId,
                ExternalFitId = refId
            });
        }

        FinalizeStatementDatesAndBalances(statement, null, null);
        return statement;
    }

    public BankStatement ParseCsv(
        string csvContent,
        string fileName,
        string glAccountCode = "1020",
        CsvBankImportMapping? mapping = null)
    {
        var rows = CsvReaderHelper.Parse(csvContent)
            .Where(r => r.Count > 0 && r.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();

        var statement = new BankStatement
        {
            GlAccountCode = glAccountCode,
            SourceFileName = fileName,
            SourceFormat = "CSV",
            StatementName = System.IO.Path.GetFileNameWithoutExtension(fileName)
        };

        if (rows.Count == 0) return statement;

        int headerRowIndex = mapping != null ? 0 : FindCsvHeaderRowIndex(rows);
        int? detectedStatusCol = null;
        int? detectedDetailsCol = null;
        var activeMap = mapping ?? AutoDetectCsvMapping(rows[headerRowIndex], out detectedStatusCol, out detectedDetailsCol);
        var startRow = activeMap.HasHeaderRow ? headerRowIndex + 1 : 0;

        for (int i = startRow; i < rows.Count; i++)
        {
            var row = rows[i];
            if (mapping == null && detectedStatusCol.HasValue)
            {
                var statusVal = GetCell(row, detectedStatusCol.Value);
                if (statusVal.Contains("en attente", StringComparison.OrdinalIgnoreCase) ||
                    statusVal.Contains("pending", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var dateStr = GetCell(row, activeMap.DateColumnIndex);
            if (!TryParseFlexibleDate(dateStr, out var txDate))
                continue;

            decimal amount = 0m;
            bool hasAmount = false;

            if (activeMap.DebitWithdrawalColumnIndex.HasValue && activeMap.CreditDepositColumnIndex.HasValue)
            {
                var debitStr = GetCell(row, activeMap.DebitWithdrawalColumnIndex.Value);
                var creditStr = GetCell(row, activeMap.CreditDepositColumnIndex.Value);
                bool hasD = TryParseDecimal(debitStr, out var debitVal);
                bool hasC = TryParseDecimal(creditStr, out var creditVal);
                if (hasD || hasC)
                {
                    amount = Math.Abs(creditVal) - Math.Abs(debitVal);
                    hasAmount = true;
                }
            }

            if (!hasAmount && activeMap.AmountColumnIndex.HasValue)
            {
                var amtStr = GetCell(row, activeMap.AmountColumnIndex.Value);
                if (TryParseDecimal(amtStr, out var parsedAmt))
                {
                    amount = parsedAmt;
                    hasAmount = true;
                }
            }

            if (!hasAmount || amount == 0m) continue;

            var desc = GetCell(row, activeMap.DescriptionColumnIndex);
            var details = detectedDetailsCol.HasValue ? GetCell(row, detectedDetailsCol.Value) : string.Empty;
            var rawPayee = activeMap.PayeeColumnIndex.HasValue
                ? GetCell(row, activeMap.PayeeColumnIndex.Value)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(details) && !desc.Contains(details, StringComparison.OrdinalIgnoreCase))
            {
                desc = string.IsNullOrWhiteSpace(desc) ? details : $"{desc} — {details}";
            }
            var payee = !string.IsNullOrWhiteSpace(rawPayee)
                ? rawPayee
                : (!string.IsNullOrWhiteSpace(details) ? details : desc);
            var reference = activeMap.ReferenceColumnIndex.HasValue
                ? GetCell(row, activeMap.ReferenceColumnIndex.Value)
                : string.Empty;
            decimal? runningBalance = null;
            if (activeMap.BalanceColumnIndex.HasValue)
            {
                var balStr = GetCell(row, activeMap.BalanceColumnIndex.Value);
                if (TryParseDecimal(balStr, out var parsedBal))
                {
                    runningBalance = Math.Round(parsedBal, 2);
                }
            }

            statement.Lines.Add(new BankTransactionLine
            {
                Date = txDate,
                Description = desc,
                Payee = !string.IsNullOrWhiteSpace(payee) ? payee : desc,
                Amount = Math.Round(amount, 2),
                RunningBalance = runningBalance,
                ReferenceNumber = reference,
                ExternalFitId = !string.IsNullOrWhiteSpace(reference)
                    ? $"{txDate:yyyyMMdd}:{reference}:{amount:F2}"
                    : $"{txDate:yyyyMMdd}:{i}:{amount:F2}"
            });
        }

        // Preserve exact intra-day chronological order when bank CSVs are exported newest-first (e.g. BNC)
        if (statement.Lines.Count >= 2 && statement.Lines[0].Date > statement.Lines[^1].Date)
        {
            statement.Lines.Reverse();
        }

        var latestWithBal = statement.Lines.LastOrDefault(l => l.RunningBalance.HasValue);
        if (latestWithBal?.RunningBalance != null)
        {
            statement.ClosingBalance = latestWithBal.RunningBalance.Value;
        }

        FinalizeStatementDatesAndBalances(statement, null, null);
        return statement;
    }

    private static int FindCsvHeaderRowIndex(List<List<string>> rows)
    {
        int limit = Math.Min(rows.Count, 6);
        for (int r = 0; r < limit; r++)
        {
            var cells = rows[r];
            bool hasDate = cells.Any(c => c.Contains("date", StringComparison.OrdinalIgnoreCase));
            bool hasAmt = cells.Any(c =>
                c.Contains("montant", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("amount", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("débit", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("debit", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("crédit", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("credit", StringComparison.OrdinalIgnoreCase));
            if (hasDate && hasAmt) return r;
        }
        return 0;
    }

    private static CsvBankImportMapping AutoDetectCsvMapping(IReadOnlyList<string> firstRow, out int? statusColumnIndex, out int? detailsColumnIndex)
    {
        statusColumnIndex = null;
        detailsColumnIndex = null;
        var map = new CsvBankImportMapping
        {
            HasHeaderRow = !TryParseFlexibleDate(firstRow[0], out _),
            DateColumnIndex = 0,
            DescriptionColumnIndex = Math.Min(1, firstRow.Count - 1),
            AmountColumnIndex = Math.Min(2, firstRow.Count - 1)
        };

        if (!map.HasHeaderRow) return map;

        int? debitIdx = null;
        int? creditIdx = null;
        int? amtIdx = null;
        bool dateSet = false;

        for (int idx = 0; idx < firstRow.Count; idx++)
        {
            var h = firstRow[idx].Trim().ToLowerInvariant();
            if (h.Contains("date"))
            {
                if (!dateSet)
                {
                    map.DateColumnIndex = idx;
                    dateSet = true;
                }
            }
            else if (h.Contains("statut") || h == "status")
                statusColumnIndex = idx;
            else if (h.Contains("desc") || h.Contains("memo") || h.Contains("libell"))
                map.DescriptionColumnIndex = idx;
            else if (h.Contains("détail") || h.Contains("detail"))
                detailsColumnIndex = idx;
            else if (h.Contains("contrepartie") || h.Contains("payee") || h.Contains("bénéficiaire") || h.Contains("nom") || h.Contains("name"))
                map.PayeeColumnIndex = idx;
            else if (h.Contains("réf") || h.Contains("ref") || h.Contains("check") || h.Contains("chèque") || h == "id")
                map.ReferenceColumnIndex = idx;
            else if (h.Contains("retrait") || h.Contains("withdrawal") || h == "debit" || h == "débit")
                debitIdx = idx;
            else if (h.Contains("dépôt") || h.Contains("depot") || h.Contains("deposit") || h == "credit" || h == "crédit")
                creditIdx = idx;
            else if (h.Contains("amount") || h.Contains("montant") || h.Contains("valeur"))
                amtIdx = idx;
            else if (h.Contains("solde") || h.Contains("balance"))
                map.BalanceColumnIndex = idx;
        }

        if (debitIdx.HasValue && creditIdx.HasValue)
        {
            map.DebitWithdrawalColumnIndex = debitIdx;
            map.CreditDepositColumnIndex = creditIdx;
            map.AmountColumnIndex = null;
        }
        else if (amtIdx.HasValue)
        {
            map.AmountColumnIndex = amtIdx;
        }

        return map;
    }

    private static void FinalizeStatementDatesAndBalances(BankStatement statement, DateTime? dtStart, DateTime? dtEnd)
    {
        statement.Lines = statement.Lines.OrderBy(l => l.Date).ToList();
        if (statement.Lines.Count > 0)
        {
            statement.StartDate = dtStart ?? statement.Lines.Min(l => l.Date);
            statement.EndDate = dtEnd ?? statement.Lines.Max(l => l.Date);
        }
        else
        {
            statement.StartDate = dtStart ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            statement.EndDate = dtEnd ?? DateTime.Today;
        }

        if (statement.ClosingBalance != 0m && statement.OpeningBalance == 0m)
        {
            statement.OpeningBalance = Math.Round(statement.ClosingBalance - statement.TotalNetMovement, 2);
        }
        else if (statement.ClosingBalance == 0m && statement.Lines.Count > 0)
        {
            statement.ClosingBalance = Math.Round(statement.OpeningBalance + statement.TotalNetMovement, 2);
        }
    }

    private static string? ExtractOfxSection(string text, string tag)
    {
        var openIdx = text.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0) return null;
        var closeIdx = text.IndexOf($"</{tag}>", openIdx, StringComparison.OrdinalIgnoreCase);
        if (closeIdx > openIdx)
        {
            return text.Substring(openIdx, closeIdx - openIdx);
        }
        return text[openIdx..];
    }

    private static string? ExtractOfxTagValue(string block, string tag)
    {
        var marker = $"<{tag}>";
        var idx = block.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = start;
        while (end < block.Length && block[end] != '<' && block[end] != '\r' && block[end] != '\n')
        {
            end++;
        }
        return block[start..end].Trim();
    }

    private static DateTime? ParseOfxDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length >= 8 &&
            DateTime.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }
        return null;
    }

    private static bool TryParseFlexibleDate(string? raw, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = raw.Trim();
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyyMMdd", "dd-MM-yyyy", "M/d/yyyy"];
        if (DateTime.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }
        return DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
               DateTime.TryParse(cleaned, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseDecimal(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim()
            .Replace("$", "")
            .Replace("€", "")
            .Replace(" ", "")
            .Replace("\u00A0", "");

        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            s = "-" + s[1..^1];
        }

        // Handle European/French comma decimal separator when no dot is present
        if (s.Contains(',') && !s.Contains('.'))
        {
            s = s.Replace(',', '.');
        }
        else if (s.Contains(',') && s.Contains('.'))
        {
            s = s.Replace(",", "");
        }

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string GetCell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static string DecodeSgmlEntities(string text) =>
        text.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Trim();
}
