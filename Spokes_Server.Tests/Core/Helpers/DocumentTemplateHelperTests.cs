using System;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Services.Accounting;

namespace Spokes_Server.Tests.Core.Helpers;

public class DocumentTemplateHelperTests
{
    [Fact]
    public void ResolveCulture_ReturnsFrenchCulture_ForExplicitAndHeuristicFrenchTemplates()
    {
        var explicitFr = new DocumentTemplate { LanguageCode = "fr-CA" };
        var heuristicFr = new DocumentTemplate
        {
            LanguageCode = "en",
            Label_FinancialStatements = "États financiers"
        };
        var english = new DocumentTemplate { Name = "Standard English", LanguageCode = "en" };

        Assert.Equal("fr", DocumentTemplateHelper.ResolveCulture(explicitFr).TwoLetterISOLanguageName);
        Assert.Equal("fr", DocumentTemplateHelper.ResolveCulture(heuristicFr).TwoLetterISOLanguageName);
        Assert.Equal("en", DocumentTemplateHelper.ResolveCulture(english).TwoLetterISOLanguageName);
    }

    [Fact]
    public void ResolveLabel_UsesFrenchFallbackOnlyWhenUnmodifiedOrBlank()
    {
        var defaultEnglishValue = new DocumentTemplate().Label_BalanceSheetSection;
        Assert.Equal(
            "Bilan (Situation financière)",
            DocumentTemplateHelper.ResolveLabel(true, defaultEnglishValue, "Balance Sheet (Statement of Financial Position)", "Bilan (Situation financière)"));

        Assert.Equal(
            "Bilan annuel vérifié",
            DocumentTemplateHelper.ResolveLabel(true, "Bilan annuel vérifié", "Balance Sheet (Statement of Financial Position)", "Bilan (Situation financière)"));

        Assert.Equal(
            "Balance Sheet (Statement of Financial Position)",
            DocumentTemplateHelper.ResolveLabel(false, null, "Balance Sheet (Statement of Financial Position)", "Bilan (Situation financière)"));
    }

    [Fact]
    public void FormatCurrency_RespectsTemplateCurrencySymbolPlacementAndSign()
    {
        var enTemplate = new DocumentTemplate { LanguageCode = "en-US", CurrencySymbol = "$", CurrencySymbolAfter = false };
        var frTemplate = new DocumentTemplate { LanguageCode = "fr-CA", CurrencySymbol = "$", CurrencySymbolAfter = true };

        Assert.Equal("-$134.86", DocumentTemplateHelper.FormatCurrency(enTemplate, -134.86m));
        Assert.Equal("-134,86 $", DocumentTemplateHelper.FormatCurrency(frTemplate, -134.86m));
        Assert.Equal("1\u00A0250,50", DocumentTemplateHelper.FormatCurrency(frTemplate, 1250.50m, includeSymbol: false));
    }

    [Fact]
    public void IsFrench_RespectsFallbackLanguageWhenNoExplicitTemplateSet()
    {
        var enTemplate = new DocumentTemplate { LanguageCode = "en" };
        Assert.True(DocumentTemplateHelper.IsFrench(enTemplate, fallbackLanguage: "FR", hasExplicitTemplate: false));
        Assert.False(DocumentTemplateHelper.IsFrench(enTemplate, fallbackLanguage: "FR", hasExplicitTemplate: true));
    }

    [Fact]
    public void FinancialStatementLine_GetLocalizedName_ReturnsFrenchWhenAvailable()
    {
        var line = new FinancialStatementLine
        {
            AccountCode = "5610",
            AccountName = "Rent, Electricity & Utilities",
            AccountNameFr = "Loyer, électricité et services publics"
        };

        Assert.Equal("Loyer, électricité et services publics", line.GetLocalizedName(true));
        Assert.Equal("Rent, Electricity & Utilities", line.GetLocalizedName(false));
    }
}
