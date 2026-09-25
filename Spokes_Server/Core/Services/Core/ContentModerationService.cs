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

        if (settings?.EnableTextFilter != true || settings.BlockedWords == null || settings.BlockedWords.Count == 0)
        {
            return Task.FromResult((false, text, settings?.TextAction ?? TextModerationAction.Block));
        }

        var validWords = settings.BlockedWords
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length)
            .ToList();

        if (validWords.Count == 0)
        {
            return Task.FromResult((false, text, settings.TextAction));
        }

        var blockedWordSet = new HashSet<string>(validWords, StringComparer.OrdinalIgnoreCase);

        var patterns = validWords.Select(word =>
        {
            var escaped = Regex.Escape(word);
            var prefix = char.IsLetterOrDigit(word[0]) || word[0] == '_' ? @"\b" : "";
            var suffix = char.IsLetterOrDigit(word[^1]) || word[^1] == '_' ? @"\b" : "";
            return $@"(?:{prefix}{escaped}{suffix})";
        });

        var combinedPattern = string.Join("|", patterns);
        bool hasViolation = false;

        var sanitizedText = Regex.Replace(text, combinedPattern, match =>
        {
            hasViolation = true;
            return GenerateRandomMask(match.Value, blockedWordSet);
        }, RegexOptions.IgnoreCase);

        return Task.FromResult((hasViolation, sanitizedText, settings.TextAction));
    }

    private static string GenerateRandomMask(string originalWord, HashSet<string> blockedWords)
    {
        if (string.IsNullOrEmpty(originalWord)) return string.Empty;

        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        int length = originalWord.Length;
        char[] buffer = new char[length];

        for (int attempt = 0; attempt < 10; attempt++)
        {
            for (int i = 0; i < length; i++)
            {
                bool isUpper = char.IsUpper(originalWord[i]);
                buffer[i] = isUpper
                    ? upper[Random.Shared.Next(upper.Length)]
                    : lower[Random.Shared.Next(lower.Length)];
            }

            var candidate = new string(buffer);
            if (!blockedWords.Contains(candidate))
            {
                return candidate;
            }
        }

        return new string(buffer);
    }
}
