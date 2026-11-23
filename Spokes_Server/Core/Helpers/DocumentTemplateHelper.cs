namespace Spokes_Server.Core.Helpers;

using System;
using System.Globalization;
using System.Linq;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Models.Documents;

/// <summary>
/// Shared helpers for resolving <see cref="DocumentTemplate"/> culture, localized default labels,
/// dates, and currency formatting consistently across Quotes, Invoices, POs, Project Reports,
/// and Financial Statements.
/// </summary>
public static class DocumentTemplateHelper
{
    public const string DefaultBasisOfPresentationEn =
        "Prepared on an accrual basis from posted General Ledger records for management, shareholder, and investor review (Compilation / Notice to Reader). Not audited or reviewed by an independent public accountant unless signed off externally.";

    public const string DefaultBasisOfPresentationFr =
        "Préparé selon la comptabilité d'exercice à partir des écritures reportées au grand livre à l'intention de la direction, des actionnaires et des investisseurs (compilation / avis au lecteur). Non audité ni examiné par un expert-comptable indépendant, sauf signature externe.";

    /// <summary>
    /// Resolves the formatting <see cref="CultureInfo"/> for a <see cref="DocumentTemplate"/>.
    /// Supports explicit <c>LanguageCode</c> as well as heuristic French detection when labels or
    /// template names are French while <c>LanguageCode</c> remains <c>"en"</c>.
    /// </summary>
    public static CultureInfo ResolveCulture(DocumentTemplate? template)
    {
        if (template == null)
        {
            return CultureInfo.CurrentUICulture;
        }

        var code = template.LanguageCode?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(code) &&
            !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(code, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException)
            {
                // Fall through to heuristic detection
            }
        }

        if (string.Equals(template.Label_Month?.Trim(), "Mois", StringComparison.OrdinalIgnoreCase) ||
            (template.Label_ProjectReport?.Contains("Rapport", StringComparison.OrdinalIgnoreCase) == true) ||
            (template.Label_FinancialStatements?.Contains("État", StringComparison.OrdinalIgnoreCase) == true) ||
            (template.Label_Invoice?.Contains("Facture", StringComparison.OrdinalIgnoreCase) == true) ||
            (template.Name?.Contains("Français", StringComparison.OrdinalIgnoreCase) == true) ||
            (template.Name?.Contains("French", StringComparison.OrdinalIgnoreCase) == true) ||
            (template.TaskTranslationLanguage?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) == true))
        {
            return CultureInfo.GetCultureInfo("fr-CA");
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            try
            {
                return CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException)
            {
                // Fall back to CurrentUICulture
            }
        }

        return CultureInfo.CurrentUICulture;
    }

    public static bool IsFrench(DocumentTemplate? template, string? fallbackLanguage = null, bool hasExplicitTemplate = true)
    {
        if (template != null &&
            string.Equals(ResolveCulture(template).TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!hasExplicitTemplate && string.Equals(fallbackLanguage, "FR", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static DocumentTemplate ResolveEffectiveTemplate(
        string? templateId,
        DocumentTemplateRepository? repository,
        string? fallbackLanguage = null)
    {
        if (repository == null)
        {
            return new DocumentTemplate
            {
                LanguageCode = string.Equals(fallbackLanguage, "FR", StringComparison.OrdinalIgnoreCase) ? "fr-CA" : "en"
            };
        }

        if (!string.IsNullOrWhiteSpace(templateId))
        {
            return repository.GetById(templateId) ?? repository.GetDefault();
        }

        if (string.Equals(fallbackLanguage, "FR", StringComparison.OrdinalIgnoreCase))
        {
            var frenchTemplate = repository.GetAll()
                .FirstOrDefault(t => string.Equals(ResolveCulture(t).TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase));
            if (frenchTemplate != null)
            {
                return frenchTemplate;
            }
        }

        return repository.GetDefault();
    }

    public static string ResolveLabel(bool isFrench, string? configuredValue, string defaultEnglish, string defaultFrench)
    {
        if (isFrench &&
            (string.IsNullOrWhiteSpace(configuredValue) ||
             string.Equals(configuredValue.Trim(), defaultEnglish.Trim(), StringComparison.Ordinal)))
        {
            return defaultFrench;
        }

        return string.IsNullOrWhiteSpace(configuredValue) ? defaultEnglish : configuredValue;
    }

    public static string FormatDate(DocumentTemplate? template, DateTime date)
    {
        var fmt = !string.IsNullOrWhiteSpace(template?.DateFormat) ? template.DateFormat : "yyyy-MM-dd";
        var culture = ResolveCulture(template);
        return date.ToString(fmt, culture);
    }

    public static string FormatCurrency(DocumentTemplate? template, decimal amount, bool includeSymbol = true)
    {
        var symbol = !string.IsNullOrWhiteSpace(template?.CurrencySymbol) ? template.CurrencySymbol : "$";
        var culture = ResolveCulture(template);
        var formattedNumber = Math.Abs(amount).ToString("N2", culture);
        var sign = amount < 0m ? "-" : string.Empty;

        if (!includeSymbol)
        {
            return $"{sign}{formattedNumber}";
        }

        return template?.CurrencySymbolAfter == true
            ? $"{sign}{formattedNumber} {symbol}"
            : $"{sign}{symbol}{formattedNumber}";
    }

    public static string FormatSignedVariance(DocumentTemplate? template, decimal variance)
    {
        var culture = ResolveCulture(template);
        var absFormatted = Math.Abs(variance).ToString("N2", culture);
        if (variance > 0m) return $"+{absFormatted}";
        if (variance < 0m) return $"-{absFormatted}";
        return 0m.ToString("N2", culture);
    }
}
