namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

/// <summary>
/// Model for a search filter autocomplete suggestion.
/// </summary>
public record SearchSuggestion(
    string DisplayText,
    string ReplacementText,
    string Description,
    string Icon,
    int ReplaceStart,
    int ReplaceLength,
    Employee? Employee = null);

/// <summary>
/// Provides smart autocomplete suggestions, synonym auto-corrections, and tag value helpers
/// for chat search queries based on the caret position.
/// </summary>
public static class ChatSearchSuggestionService
{
    private record TagDefinition(
        string Canonical,
        string Replacement,
        string Description,
        string Icon,
        string[] Synonyms);

    private static readonly TagDefinition[] FilterDefinitions =
    [
        new("from:", "from:", "Filter by sender (e.g. from:admin, from:me)", "Icons.Material.Filled.Person", ["sender:", "author:", "who:", "by:", "sender", "author"]),
        new("isPicture:", "isPicture: ", "Messages with images, photos or GIFs", "Icons.Material.Filled.Image", ["image:", "photo:", "pic:", "img:", "picture:", "is:image", "has:image", "photo", "pic", "image"]),
        new("has:file", "has:file ", "Messages with attached files & documents", "Icons.Material.Filled.AttachFile", ["file:", "isfile:", "attachment:", "attach:", "doc:", "is:file", "file", "attachment", "doc"]),
        new("before:", "before:", "Sent before date (e.g. before:today, before:2026-10-01)", "Icons.Material.Filled.CalendarToday", ["until:", "older:", "until", "older"]),
        new("after:", "after:", "Sent after date (e.g. after:yesterday, after:2026-01-01)", "Icons.Material.Filled.Update", ["since:", "newer:", "since", "newer"]),
        new("has:link", "has:link ", "Messages containing web links", "Icons.Material.Filled.Link", ["link:", "url:", "web:", "website:", "link", "url"]),
        new("mentions:", "mentions:", "Filter messages mentioning a member", "Icons.Material.Filled.AlternateEmail", ["to:", "mention:", "mention"]),
        new("has:reaction", "has:reaction ", "Messages with emoji reactions", "Icons.Material.Filled.AddReaction", ["reaction:", "react:", "reactions:"])
    ];

    private static readonly HashSet<string> ValuePrefixTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "from:", "author:", "sender:", "who:", "by:",
        "mentions:", "mention:", "to:",
        "before:", "until:", "older:",
        "after:", "since:", "newer:",
        "has:", "is:"
    };

    public static List<SearchSuggestion> GetSuggestions(
        string? query,
        int cursorPosition,
        string? currentUserId,
        IEnumerable<Employee> channelMembers,
        Func<Employee, bool>? canSeeEmail = null)
    {
        var suggestions = new List<SearchSuggestion>();
        var (token, tokenStart, tokenLength) = ExtractTokenAtCursor(query, cursorPosition);
        var visibleMembers = channelMembers.Where(m =>
            m.IsActive && !m.IsSystem && !m.IsBanned && m.Id != AccountDeletionService.SystemDeletedUserId);

        bool CanMatchEmail(Employee m) =>
            canSeeEmail != null
                ? canSeeEmail(m)
                : (m.IsEmailPublic || (!string.IsNullOrEmpty(currentUserId) && string.Equals(m.Id, currentUserId, StringComparison.Ordinal)));

        // 1. If currently inside a "from:" tag value: suggest channel members + "from:me" + "from:admin"
        if (token.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
        {
            var valuePrefix = token[5..].Trim('"').TrimStart().ToLowerInvariant();
            AddMemberTagSuggestions(suggestions, visibleMembers, CanMatchEmail, "from", "My own messages", "Icons.Material.Filled.PersonPin", "Icons.Material.Filled.AccountCircle", valuePrefix, tokenStart, tokenLength);
            return suggestions.Take(8).ToList();
        }

        // 2. If currently inside a "mentions:" tag value: suggest channel members + "mentions:me"
        if (token.StartsWith("mentions:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("to:", StringComparison.OrdinalIgnoreCase))
        {
            int colonIdx = token.IndexOf(':');
            var valuePrefix = token[(colonIdx + 1)..].Trim('"').TrimStart().ToLowerInvariant();
            AddMemberTagSuggestions(suggestions, visibleMembers, CanMatchEmail, "mentions", "Messages mentioning me", "Icons.Material.Filled.AlternateEmail", "Icons.Material.Filled.AlternateEmail", valuePrefix, tokenStart, tokenLength);
            return suggestions.Take(8).ToList();
        }

        // 3. If currently inside a "before:" or "after:" tag value: suggest date presets & syntax examples
        if (token.StartsWith("before:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
        {
            int colonIdx = token.IndexOf(':');
            var prefix = token[..(colonIdx + 1)];
            var valPart = token[(colonIdx + 1)..].TrimStart().ToLowerInvariant();
            var today = DateTime.UtcNow;
            var todayStr = today.ToString("yyyy-MM-dd");
            var monthStartStr = today.ToString("yyyy-MM-01");
            bool isBefore = prefix.Equals("before:", StringComparison.OrdinalIgnoreCase);

            var dateOptions = new List<(string Display, string Value, string Desc, string Icon)>
            {
                ($"{prefix}today", $"{prefix}today ", isBefore ? "Messages before end of today" : "Messages sent today (since midnight)", "Icons.Material.Filled.Today"),
                ($"{prefix}yesterday", $"{prefix}yesterday ", isBefore ? "Messages before yesterday" : "Messages since yesterday", "Icons.Material.Filled.History"),
                ($"{prefix}thisweek", $"{prefix}thisweek ", isBefore ? "Messages before this week" : "Messages since Monday", "Icons.Material.Filled.DateRange"),
                ($"{prefix}lastweek", $"{prefix}lastweek ", isBefore ? "Messages before last week" : "Messages since last week", "Icons.Material.Filled.CalendarMonth"),
                ($"{prefix}{todayStr}", $"{prefix}{todayStr} ", "Date syntax example (YYYY-MM-DD)", "Icons.Material.Filled.CalendarToday"),
                ($"{prefix}{monthStartStr}", $"{prefix}{monthStartStr} ", "Start of month (YYYY-MM-01)", "Icons.Material.Filled.Event")
            };

            foreach (var opt in dateOptions)
            {
                if (string.IsNullOrEmpty(valPart) || opt.Display.ToLowerInvariant().Contains(valPart) || opt.Value.ToLowerInvariant().Contains(valPart))
                {
                    suggestions.Add(new SearchSuggestion(opt.Display, opt.Value, opt.Desc, opt.Icon, tokenStart, tokenLength));
                }
            }
            return suggestions;
        }

        // 4. If currently inside "has:" or "is:": suggest valid sub-tags
        if (token.StartsWith("has:", StringComparison.OrdinalIgnoreCase))
        {
            var valPart = token[4..].TrimStart().ToLowerInvariant();
            var hasOptions = new List<(string Display, string Value, string Desc, string Icon)>
            {
                ("has:file", "has:file ", "Messages with attached files & documents", "Icons.Material.Filled.AttachFile"),
                ("has:link", "has:link ", "Messages containing web links", "Icons.Material.Filled.Link"),
                ("has:image", "isPicture: ", "Messages with pictures, photos or GIFs", "Icons.Material.Filled.Image"),
                ("has:reaction", "has:reaction ", "Messages with emoji reactions", "Icons.Material.Filled.AddReaction")
            };
            foreach (var opt in hasOptions)
            {
                if (string.IsNullOrEmpty(valPart) || opt.Display.ToLowerInvariant().Contains(valPart))
                {
                    suggestions.Add(new SearchSuggestion(opt.Display, opt.Value, opt.Desc, opt.Icon, tokenStart, tokenLength));
                }
            }
            return suggestions;
        }

        if (token.StartsWith("is:", StringComparison.OrdinalIgnoreCase))
        {
            var valPart = token[3..].TrimStart().ToLowerInvariant();
            var isOptions = new List<(string Display, string Value, string Desc, string Icon)>
            {
                ("isPicture:", "isPicture: ", "Messages with images or GIFs", "Icons.Material.Filled.Image"),
                ("isFile:", "has:file ", "Messages with attached files & documents", "Icons.Material.Filled.AttachFile")
            };
            foreach (var opt in isOptions)
            {
                if (string.IsNullOrEmpty(valPart) || opt.Display.ToLowerInvariant().Contains(valPart))
                {
                    suggestions.Add(new SearchSuggestion(opt.Display, opt.Value, opt.Desc, opt.Icon, tokenStart, tokenLength));
                }
            }
            return suggestions;
        }

        // 5. Synonym detection & canonical tag suggestions
        var lowerToken = token.ToLowerInvariant();

        if (!string.IsNullOrEmpty(lowerToken))
        {
            foreach (var def in FilterDefinitions)
            {
                // Exact or prefix match on canonical tag
                if (def.Canonical.ToLowerInvariant().StartsWith(lowerToken))
                {
                    suggestions.Add(new SearchSuggestion(def.Canonical, def.Replacement, def.Description, def.Icon, tokenStart, tokenLength));
                    continue;
                }

                // Check synonyms
                if (def.Synonyms.Any(syn => syn.StartsWith(lowerToken) || lowerToken.StartsWith(syn.TrimEnd(':'))))
                {
                    suggestions.Add(new SearchSuggestion(
                        def.Canonical,
                        def.Replacement,
                        $"{def.Description} (converts '{token}')",
                        def.Icon,
                        tokenStart,
                        tokenLength));
                }
            }
        }

        // If query is empty or no token, or user typed ':', show all canonical tags
        if (suggestions.Count == 0 && (string.IsNullOrEmpty(token) || token == ":"))
        {
            foreach (var def in FilterDefinitions)
            {
                suggestions.Add(new SearchSuggestion(def.Canonical, def.Replacement, def.Description, def.Icon, tokenStart, tokenLength));
            }
        }

        return suggestions.Take(7).ToList();
    }

    private static (string Token, int Start, int Length) ExtractTokenAtCursor(string? query, int cursor)
    {
        if (string.IsNullOrEmpty(query)) return ("", 0, 0);
        cursor = Math.Clamp(cursor, 0, query.Length);

        int start = cursor;
        while (start > 0 && !char.IsWhiteSpace(query[start - 1])) start--;

        int end = cursor;
        while (end < query.Length && !char.IsWhiteSpace(query[end])) end++;

        var token = query[start..end];
        var tokenStart = start;
        var tokenLength = end - start;

        // If the cursor is inside or right after a word, check if the preceding word was a tag prefix
        // (e.g. "from: admin" or "from: " where the user added a space after the colon)
        if (start > 0)
        {
            int prevWordEnd = start;
            while (prevWordEnd > 0 && char.IsWhiteSpace(query[prevWordEnd - 1])) prevWordEnd--;
            int prevWordStart = prevWordEnd;
            while (prevWordStart > 0 && !char.IsWhiteSpace(query[prevWordStart - 1])) prevWordStart--;

            var prevWord = query[prevWordStart..prevWordEnd];
            if (ValuePrefixTags.Contains(prevWord))
            {
                token = prevWord + token;
                tokenStart = prevWordStart;
                tokenLength = end - prevWordStart;
            }
        }
        else if (string.IsNullOrEmpty(token) && cursor > 0)
        {
            var textBeforeCursor = query[..cursor].TrimEnd();
            var lastWord = textBeforeCursor.Split(' ').LastOrDefault() ?? "";
            if (lastWord.EndsWith(':') && !lastWord.Equals("isPicture:", StringComparison.OrdinalIgnoreCase))
            {
                token = lastWord;
                tokenStart = textBeforeCursor.LastIndexOf(lastWord, StringComparison.Ordinal);
                tokenLength = cursor - tokenStart;
            }
        }

        return (token, tokenStart, tokenLength);
    }

    private static void AddMemberTagSuggestions(
        List<SearchSuggestion> suggestions,
        IEnumerable<Employee> visibleMembers,
        Func<Employee, bool> canMatchEmail,
        string tagPrefix,
        string meDescription,
        string meIcon,
        string memberIcon,
        string valuePrefix,
        int tokenStart,
        int tokenLength)
    {
        if (string.IsNullOrEmpty(valuePrefix) || "me".StartsWith(valuePrefix))
        {
            suggestions.Add(new SearchSuggestion($"{tagPrefix}:me", $"{tagPrefix}:me ", meDescription, meIcon, tokenStart, tokenLength));
        }

        if (string.IsNullOrEmpty(valuePrefix) || "admin".StartsWith(valuePrefix) || "administrator".StartsWith(valuePrefix))
        {
            suggestions.Add(new SearchSuggestion($"{tagPrefix}:admin", $"{tagPrefix}:admin ", "Administrator accounts", "Icons.Material.Filled.AdminPanelSettings", tokenStart, tokenLength));
        }

        foreach (var m in visibleMembers)
        {
            var fullName = m.FullName;
            if (string.IsNullOrEmpty(valuePrefix) ||
                fullName.ToLowerInvariant().Contains(valuePrefix) ||
                (!string.IsNullOrEmpty(m.FirstName) && m.FirstName.ToLowerInvariant().Contains(valuePrefix)) ||
                (!string.IsNullOrEmpty(m.LastName) && m.LastName.ToLowerInvariant().Contains(valuePrefix)) ||
                (canMatchEmail(m) && !string.IsNullOrEmpty(m.Email) && m.Email.ToLowerInvariant().Contains(valuePrefix)) ||
                (!string.IsNullOrEmpty(m.Position) && m.Position.ToLowerInvariant().Contains(valuePrefix)))
            {
                var formattedValue = fullName.Contains(' ') ? $"{tagPrefix}:\"{fullName}\" " : $"{tagPrefix}:{fullName} ";
                var subtitle = !string.IsNullOrWhiteSpace(m.DisplaySubtitle) ? m.DisplaySubtitle : "Member";
                suggestions.Add(new SearchSuggestion(
                    fullName,
                    formattedValue,
                    subtitle,
                    memberIcon,
                    tokenStart,
                    tokenLength,
                    m));
            }
        }
    }
}
