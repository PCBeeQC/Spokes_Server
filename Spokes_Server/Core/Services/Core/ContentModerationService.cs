using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Core;

public interface IContentModerationService
{
    Task<(bool HasViolation, string SanitizedText, TextModerationAction Action)> EvaluateTextAsync(string text);
}

public class ContentModerationService : IContentModerationService
{
    private readonly CompanyProfileRepository _companyProfiles;

    public ContentModerationService(CompanyProfileRepository companyProfiles)
    {
        _companyProfiles = companyProfiles;
    }

    public Task<(bool HasViolation, string SanitizedText, TextModerationAction Action)> EvaluateTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult((false, text, TextModerationAction.Block));
        }

        var profile = _companyProfiles.Get();
        var settings = profile?.Moderation;

        if (settings == null || !settings.EnableTextFilter || settings.BlockedWords == null || !settings.BlockedWords.Any())
        {
            return Task.FromResult((false, text, TextModerationAction.Block));
        }

        bool hasViolation = false;
        string sanitizedText = text;

        foreach (var word in settings.BlockedWords)
        {
            if (string.IsNullOrWhiteSpace(word)) continue;

            // Use regex for whole word match, case-insensitive
            var pattern = $@"\b{Regex.Escape(word)}\b";
            if (Regex.IsMatch(sanitizedText, pattern, RegexOptions.IgnoreCase))
            {
                hasViolation = true;
                sanitizedText = Regex.Replace(sanitizedText, pattern, "***", RegexOptions.IgnoreCase);
            }
        }

        return Task.FromResult((hasViolation, sanitizedText, settings.TextAction));
    }
}
