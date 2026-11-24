namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

public sealed record PartyMatchCandidate(
    string Name,
    string? Email = null,
    string? Phone = null,
    string? TaxId = null,
    string? PostalCode = null);

public sealed record PartyMatchResult<T>(T MatchedEntity, decimal Score, string MatchReason);

public sealed record GlAccountSuggestion(string? SuggestedCode, decimal Confidence, string Reason);

/// <summary>
/// Shared normalization, entity matching, and bilingual GL mapping suggestion helpers for accounting migrations.
/// Works uniformly across <see cref="Supplier"/>, <see cref="Projects.Project.Client"/>, <see cref="Employee"/>, and <see cref="GlAccount"/>.
/// </summary>
public static partial class PartyMatchingHelper
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "ltd", "limited", "ltee", "corp", "corporation",
        "co", "company", "cie", "llc", "llp", "lp", "enr", "senc", "srl",
        "sa", "sas", "sarl", "eurl", "gmbh", "ag", "bv", "nv", "plc"
    };

    [GeneratedRegex(@"[^\p{L}\p{N}\s]+", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphanumericRegex();

    public static string StripDiacritics(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeCompanyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var clean = StripDiacritics(name).ToLowerInvariant();
        clean = PunctuationRegex().Replace(clean, " ");
        clean = MultiSpaceRegex().Replace(clean, " ").Trim();
        if (string.IsNullOrEmpty(clean)) return string.Empty;

        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens);
    }

    public static string NormalizePersonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var raw = name.Trim();
        // Sage / Payroll exports frequently format names as "Last, First"
        if (raw.Contains(','))
        {
            var parts = raw.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                raw = $"{parts[1]} {parts[0]}";
            }
        }

        var clean = StripDiacritics(raw).ToLowerInvariant();
        clean = PunctuationRegex().Replace(clean, " ");
        return MultiSpaceRegex().Replace(clean, " ").Trim();
    }

    public static string NormalizeTokenSortedName(string? name)
    {
        var norm = NormalizePersonName(name);
        if (string.IsNullOrEmpty(norm)) return string.Empty;
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', tokens);
    }

    private static readonly string[] TaxAgencyKeywords =
    [
        "agence du revenu",
        "ministre du revenu",
        "receveur general",
        "revenu quebec",
        "canada revenue",
        "receiver general",
        "csst",
        "cnesst",
        "commission des normes",
        "commission sante et securite"
    ];

    public static bool IsTaxAuthorityOrGovernmentVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var norm = StripDiacritics(name).ToLowerInvariant();
        foreach (var kw in TaxAgencyKeywords)
        {
            if (norm.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static (string? SupplierId, string? ClientName) MatchProjectToClient(
        string rawProjectName,
        string? extractedCode,
        IReadOnlyList<PartyMigrationRow> customers,
        IReadOnlyList<Supplier> existingSuppliers)
    {
        if (string.IsNullOrWhiteSpace(rawProjectName) && string.IsNullOrWhiteSpace(extractedCode))
            return (null, null);

        var haystack = StripDiacritics($"{rawProjectName} {extractedCode}").ToLowerInvariant();
        var bestLen = 0;
        string? bestSupplierId = null;
        string? bestName = null;

        void Consider(string? id, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var norm = NormalizeCompanyName(name);
            if (norm.Length < 3) return;

            var firstToken = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            bool matched = haystack.Contains(norm, StringComparison.Ordinal) ||
                           (firstToken.Length >= 3 && haystack.Split(' ', '-', '_').Any(w => w == firstToken));

            if (matched && norm.Length > bestLen)
            {
                bestLen = norm.Length;
                bestSupplierId = id;
                bestName = name;
            }
        }

        foreach (var c in customers.Where(p => p.IsCustomer || p.IsAlsoClient))
        {
            Consider(c.MatchedEntityId, c.ContactInfo.BusinessName);
        }

        foreach (var s in existingSuppliers)
        {
            Consider(s.Id, s.Name);
        }

        // Prefix fallback for common engineering project codes (e.g. ABB 064, BOM 001, VAL 001, PRE 001)
        if (bestName == null && !string.IsNullOrWhiteSpace(extractedCode))
        {
            var alphaPrefix = new string(extractedCode.TakeWhile(char.IsLetter).ToArray());
            if (alphaPrefix.Length >= 3)
            {
                foreach (var c in customers.Where(p => p.IsCustomer || p.IsAlsoClient))
                {
                    var norm = NormalizeCompanyName(c.ContactInfo.BusinessName);
                    if (norm.StartsWith(alphaPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return (c.MatchedEntityId, c.ContactInfo.BusinessName);
                    }
                }
            }
        }

        return (bestSupplierId, bestName);
    }

    public static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return NonAlphanumericRegex().Replace(StripDiacritics(value).ToLowerInvariant(), string.Empty);
    }

    public static PartyMatchResult<T>? FindBestPartyMatch<T>(
        PartyMatchCandidate candidate,
        IEnumerable<T> existingItems,
        Func<T, PartyContactInfo?> contactSelector,
        Func<T, string?>? fallbackNameSelector = null)
        where T : class
    {
        var candNormName = NormalizeCompanyName(candidate.Name);
        var candEmail = candidate.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var candPhone = NormalizeIdentifier(candidate.Phone);
        var candTaxId = NormalizeIdentifier(candidate.TaxId);
        var candZip = NormalizeIdentifier(candidate.PostalCode);

        PartyMatchResult<T>? best = null;

        foreach (var item in existingItems)
        {
            var contact = contactSelector(item);
            var existingRawName = contact?.BusinessName;
            if (string.IsNullOrWhiteSpace(existingRawName) && fallbackNameSelector != null)
            {
                existingRawName = fallbackNameSelector(item);
            }

            var existingNormName = NormalizeCompanyName(existingRawName);
            var existingEmail = contact?.ContactPersonEmail?.Trim().ToLowerInvariant() ?? string.Empty;
            var existingPhone = NormalizeIdentifier(contact?.ContactPersonPhone);
            var existingTaxId = NormalizeIdentifier(contact?.TaxId);
            var existingZip = NormalizeIdentifier(contact?.BusinessAddressZip);

            decimal score = 0m;
            var reason = string.Empty;

            if (!string.IsNullOrEmpty(candTaxId) && candTaxId.Length >= 6 && candTaxId == existingTaxId)
            {
                score = 0.99m;
                reason = "Tax ID / NEQ / BN exact match";
            }
            else if (!string.IsNullOrEmpty(candEmail) && candEmail == existingEmail)
            {
                score = 0.97m;
                reason = "Email exact match";
            }
            else if (!string.IsNullOrEmpty(candNormName) && candNormName == existingNormName)
            {
                score = string.Equals(candidate.Name.Trim(), existingRawName?.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? 0.96m
                    : 0.94m;
                reason = "Normalized company name match";
            }
            else if (!string.IsNullOrEmpty(candPhone) && candPhone.Length >= 7 &&
                     (candPhone == existingPhone || (candPhone.Length >= 10 && existingPhone.Length >= 10 && candPhone[^10..] == existingPhone[^10..])))
            {
                var sim = ComputeNameSimilarity(candNormName, existingNormName);
                if (sim >= 0.45m)
                {
                    score = 0.90m;
                    reason = "Phone + name match";
                }
            }
            else if (!string.IsNullOrEmpty(candNormName) && !string.IsNullOrEmpty(existingNormName))
            {
                var sim = ComputeNameSimilarity(candNormName, existingNormName);
                if (!string.IsNullOrEmpty(candZip) && candZip == existingZip && sim >= 0.65m)
                {
                    score = Math.Min(0.92m, sim + 0.15m);
                    reason = "Postal code + name match";
                }
                else if (sim >= 0.82m)
                {
                    score = Math.Round(sim, 2);
                    reason = "Fuzzy company name match";
                }
            }

            if (score > 0m && (best == null || score > best.Score))
            {
                best = new PartyMatchResult<T>(item, score, reason);
            }
        }

        return best;
    }

    public static PartyMatchResult<Employee>? FindBestEmployeeMatch(
        string sourceName,
        string? sourceEmail,
        IEnumerable<Employee> employees)
    {
        var candEmail = sourceEmail?.Trim().ToLowerInvariant() ?? string.Empty;
        var candNormName = NormalizePersonName(sourceName);
        PartyMatchResult<Employee>? best = null;

        foreach (var emp in employees)
        {
            var empEmail = emp.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            var fullNorm = NormalizePersonName(emp.FullName);
            var revNorm = NormalizePersonName($"{emp.LastName} {emp.FirstName}");

            decimal score = 0m;
            var reason = string.Empty;

            if (!string.IsNullOrEmpty(candEmail) && candEmail == empEmail)
            {
                score = 0.99m;
                reason = "Employee email match";
            }
            else if (!string.IsNullOrEmpty(candNormName) && candNormName == fullNorm)
            {
                score = 0.95m;
                reason = "Employee name match";
            }
            else if (!string.IsNullOrEmpty(candNormName) && candNormName == revNorm)
            {
                score = 0.92m;
                reason = "Employee Last/First name match";
            }
            else if (!string.IsNullOrEmpty(candNormName) && !string.IsNullOrEmpty(fullNorm))
            {
                var sim = ComputeNameSimilarity(candNormName, fullNorm);
                if (sim >= 0.80m)
                {
                    score = Math.Round(sim, 2);
                    reason = "Fuzzy employee name match";
                }
            }

            if (score > 0m && (best == null || score > best.Score))
            {
                best = new PartyMatchResult<Employee>(emp, score, reason);
            }
        }

        return best;
    }

    public static GlAccountSuggestion SuggestGlAccount(
        string sourceCode,
        string sourceName,
        string? sourceTypeOrGifi,
        IReadOnlyList<GlAccount> spokesAccounts)
    {
        if (spokesAccounts.Count == 0)
            return new GlAccountSuggestion(null, 0m, "No Chart of Accounts loaded");

        var code = sourceCode?.Trim() ?? string.Empty;
        var gifi = sourceTypeOrGifi?.Trim() ?? string.Empty;
        var haystack = StripDiacritics($"{sourceName} {gifi}").ToLowerInvariant();
        string? inferredRole = InferSystemRoleFromText(haystack, code);
        var candNorm = StripDiacritics(sourceName).ToLowerInvariant();

        // 1. Singleton system control accounts FIRST (e.g., AR, AP, Retained Earnings, TPS/TVQ Collected & Recoverable)
        // so Sage 2310/2315/2340/2345/2365 map cleanly to Spokes 2160/2165/2170/2175/2180 without code collisions.
        if (!string.IsNullOrEmpty(inferredRole) && SingletonControlRoles.Contains(inferredRole))
        {
            var byRole = spokesAccounts.FirstOrDefault(a => string.Equals(a.SystemRole, inferredRole, StringComparison.OrdinalIgnoreCase));
            if (byRole != null)
                return new GlAccountSuggestion(byRole.Code, 0.92m, $"Matched control account ({inferredRole})");
        }

        // 2. Exact GL code match — ONLY when semantically compatible with the existing Spokes account
        // (prevents blind collisions like Sage 2160 Corporate Tax -> Spokes 2160 TPS Collected, or 5610 Accounting -> 5610 Rent)
        var exactCode = spokesAccounts.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));
        if (exactCode != null)
        {
            var codeNameSim = Math.Max(
                ComputeNameSimilarity(candNorm, StripDiacritics(exactCode.Name).ToLowerInvariant()),
                ComputeNameSimilarity(candNorm, StripDiacritics(exactCode.NameFr).ToLowerInvariant()));

            bool roleMatches = !string.IsNullOrEmpty(exactCode.SystemRole) &&
                               string.Equals(exactCode.SystemRole, inferredRole, StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(exactCode.SystemRole) || roleMatches || codeNameSim >= 0.45m)
            {
                return new GlAccountSuggestion(exactCode.Code, 1.00m, $"Exact code match ({exactCode.Code})");
            }

            // Incompatible collision with a Spokes system account — do NOT silently map into the wrong system account
            return new GlAccountSuggestion(null, 0m, $"Code {code} collides with Spokes '{exactCode.Name}' — creating separate account");
        }

        // 3. Exact or near-exact normalized name match against existing Spokes English/French names
        GlAccount? bestAcct = null;
        decimal bestSim = 0m;
        foreach (var acct in spokesAccounts)
        {
            var enSim = ComputeNameSimilarity(candNorm, StripDiacritics(acct.Name).ToLowerInvariant());
            var frSim = ComputeNameSimilarity(candNorm, StripDiacritics(acct.NameFr).ToLowerInvariant());
            var sim = Math.Max(enSim, frSim);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestAcct = acct;
            }
        }

        if (bestAcct != null && bestSim >= 0.88m)
            return new GlAccountSuggestion(bestAcct.Code, Math.Round(bestSim, 2), $"Exact name match ({bestAcct.Code})");

        return new GlAccountSuggestion(null, 0m, "Create new account preserving Sage code & GIFI");
    }

    private static readonly HashSet<string> SingletonControlRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        GlSystemRole.AccountsReceivable,
        GlSystemRole.AccountsPayable,
        GlSystemRole.OpeningBalanceEquity,
        GlSystemRole.RetainedEarnings,
        GlSystemRole.FederalTaxCollected,
        GlSystemRole.FederalTaxRecoverable,
        GlSystemRole.RegionalTaxCollected,
        GlSystemRole.RegionalTaxRecoverable,
        GlSystemRole.SalesTaxRemittancePayable
    };

    private static string? InferSystemRoleFromText(string text, string code)
    {
        if (ContainsAny(text, "retained earnings", "benefices non repartis", "report a nouveau"))
            return GlSystemRole.RetainedEarnings;
        if (ContainsAny(text, "share capital", "capital-actions", "capital actions", "owner equity", "avoir du proprietaire", "apport"))
            return GlSystemRole.OwnerEquity;
        if (ContainsAny(text, "opening balance", "capital d ouverture", "solde d ouverture"))
            return GlSystemRole.OpeningBalanceEquity;

        if (ContainsAny(text, "accounts receivable", "comptes clients", "clients a recevoir", "receivable"))
            return GlSystemRole.AccountsReceivable;
        if (ContainsAny(text, "accounts payable", "comptes fournisseurs", "fournisseurs a payer", "payable") && !ContainsAny(text, "tax", "tps", "tvq", "gst", "qst", "das", "wages", "salaires", "vacation", "vacances"))
            return GlSystemRole.AccountsPayable;

        if (ContainsAny(text, "chequing", "checking", "operating bank", "compte bancaire courant", "banque", "encaisse", "cash in bank") || code is "1020" or "1060")
            return GlSystemRole.OperatingBank;
        if (ContainsAny(text, "credit card", "carte de credit", "visa", "mastercard", "amex"))
            return GlSystemRole.CorporateCreditCard;
        if (ContainsAny(text, "prepaid", "frais payes d avance"))
            return GlSystemRole.PrepaidExpenses;

        if (ContainsAny(text, "tps-tvq a payer", "tps tvq a payer", "tps/tvq a payer", "remise tps", "sales tax remittance", "gst/qst payable"))
            return GlSystemRole.SalesTaxRemittancePayable;
        if (ContainsAny(text, "tps percu", "tps/tvh percu", "tps collect", "gst collect", "gst charged") && !ContainsAny(text, "taux 2"))
            return GlSystemRole.FederalTaxCollected;
        if (ContainsAny(text, "cti", "itc", "tps paye", "tps/tvh paye", "tps a recevoir", "gst paid", "gst input"))
            return GlSystemRole.FederalTaxRecoverable;
        if (ContainsAny(text, "tvq percu", "tvq collect", "qst collect", "qst charged", "pst collect") && !ContainsAny(text, "taux 2"))
            return GlSystemRole.RegionalTaxCollected;
        if (ContainsAny(text, "rti", "itr", "tvq paye", "tvq a recevoir", "qst paid", "qst input"))
            return GlSystemRole.RegionalTaxRecoverable;

        if (ContainsAny(text, "source deduction", "deductions a la source", "das", "payroll tax payable", "retenues a la source"))
            return GlSystemRole.PayrollDeductionsPayable;
        if (ContainsAny(text, "wages payable", "salaires a payer", "net pay payable"))
            return GlSystemRole.WagesPayable;
        if (ContainsAny(text, "vacation payable", "vacances a payer", "vacances accumulees"))
            return GlSystemRole.VacationPayAccrued;

        if (ContainsAny(text, "consulting", "service revenue", "services professionnels", "honoraires"))
            return GlSystemRole.ServiceRevenue;
        if (ContainsAny(text, "material", "equipment sales", "ventes de materiel", "marchandises"))
            return GlSystemRole.MaterialRevenue;
        if (ContainsAny(text, "interest income", "revenus d interets", "miscellaneous income"))
            return GlSystemRole.InterestAndOtherIncome;

        if (ContainsAny(text, "subcontract", "sous-traitance", "sous traitance"))
            return GlSystemRole.DirectSubcontractors;
        if (ContainsAny(text, "commission"))
            return GlSystemRole.CommissionsExpense;
        if (ContainsAny(text, "employer payroll", "charges sociales", "rrq", "qpp", "rqap", "qpip", "fss", "cnesst", "cpp employer"))
            return GlSystemRole.EmployerPayrollTaxesExpense;
        if (ContainsAny(text, "wages", "salaries", "salaires bruts", "payroll expense"))
            return GlSystemRole.WagesAndSalariesExpense;
        if (ContainsAny(text, "rent", "loyer", "hydro", "utilities", "electricite"))
            return GlSystemRole.RentAndUtilitiesExpense;
        if (ContainsAny(text, "software", "saas", "logiciel", "abonnement"))
            return GlSystemRole.SoftwareAndSubscriptionsExpense;
        if (ContainsAny(text, "travel", "deplacement", "meals", "repas", "mileage", "kilometrage"))
            return GlSystemRole.TravelAndMileageExpense;
        if (ContainsAny(text, "bank charge", "bank fee", "frais bancaires"))
            return GlSystemRole.BankChargesAndInterest;
        if (ContainsAny(text, "foreign exchange", "taux de change", "fx gain"))
            return GlSystemRole.ForeignExchangeGainLoss;
        if (ContainsAny(text, "office", "frais de bureau", "fournitures de bureau"))
            return GlSystemRole.OfficeAndOverheadExpense;

        return null;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (text.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static decimal ComputeNameSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0m;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1m;

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal jaccard = 0m;
        if (tokensA.Count > 0 && tokensB.Count > 0)
        {
            var inter = tokensA.Intersect(tokensB, StringComparer.OrdinalIgnoreCase).Count();
            var union = tokensA.Union(tokensB, StringComparer.OrdinalIgnoreCase).Count();
            jaccard = union > 0 ? (decimal)inter / union : 0m;
        }

        var dist = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        var levenshteinSim = maxLen > 0 ? 1m - ((decimal)dist / maxLen) : 0m;

        return Math.Max(jaccard, levenshteinSim);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        var prev = new int[t.Length + 1];
        var curr = new int[t.Length + 1];

        for (var j = 0; j <= t.Length; j++) prev[j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = char.ToLowerInvariant(s[i - 1]) == char.ToLowerInvariant(t[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[t.Length];
    }
}
