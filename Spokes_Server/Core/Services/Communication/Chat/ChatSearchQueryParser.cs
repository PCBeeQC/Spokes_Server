namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Parses raw chat search query strings into structured <see cref="ChatSearchFilter"/> instances.
/// Normalizes synonyms (e.g. photo:, pic:, author:, attachment:) to canonical filter representations.
/// </summary>
public static class ChatSearchQueryParser
{
    private static readonly Regex TokenRegex = new(
        @"(?:(?<neg>[-!])?(?<tag>[a-zA-Z_]+):(?<val>""[^""]*""|[^\s""]*))|(?<term>""[^""]+""|[^\s""]+)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ValueExpectingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "author", "sender", "who", "by",
        "to", "mention", "mentions",
        "before", "until", "older", "to_date",
        "after", "since", "newer", "from_date",
        "reaction", "react"
    };

    public static ChatSearchFilter Parse(string? query)
    {
        var filter = new ChatSearchFilter { RawQuery = query ?? string.Empty };
        if (string.IsNullOrWhiteSpace(query)) return filter;

        var matches = TokenRegex.Matches(query);
        (string Tag, bool IsNegated)? pendingTag = null;

        foreach (Match match in matches)
        {
            if (match.Groups["tag"].Success)
            {
                // Discard any preceding tag that was waiting for a value since another tag arrived
                pendingTag = null;

                var tag = match.Groups["tag"].Value.ToLowerInvariant();
                var rawVal = match.Groups["val"].Value.Trim('"');
                var isNegated = match.Groups["neg"].Success;

                if (string.IsNullOrEmpty(rawVal) && ValueExpectingTags.Contains(tag))
                {
                    // Tag expects an argument (e.g. "from: admin", "before: 2026-10-25")
                    pendingTag = (tag, isNegated);
                }
                else
                {
                    ApplyTag(filter, tag, rawVal, isNegated);
                }
            }
            else if (match.Groups["term"].Success)
            {
                var term = match.Groups["term"].Value.Trim('"').Trim();
                if (!string.IsNullOrEmpty(term))
                {
                    if (pendingTag.HasValue)
                    {
                        ApplyTag(filter, pendingTag.Value.Tag, term, pendingTag.Value.IsNegated);
                        pendingTag = null;
                    }
                    else
                    {
                        filter.TextTerms.Add(term);
                    }
                }
            }
        }

        return filter;
    }

    private static void ApplyTag(ChatSearchFilter filter, string tag, string val, bool isNegated)
    {
        switch (tag)
        {
            // Senders / Authors
            case "from" or "author" or "sender" or "who" or "by":
                if (!string.IsNullOrWhiteSpace(val))
                    filter.SenderIdentifiers.Add(val);
                break;

            // Mentions
            case "to" or "mention" or "mentions":
                if (!string.IsNullOrWhiteSpace(val))
                    filter.MentionIdentifiers.Add(val);
                break;

            // Media / Images
            case "ispicture" or "picture" or "image" or "photo" or "pic" or "img":
                filter.HasImages = isNegated ? false : ParseBooleanOrTrue(val);
                break;

            case "is" when val.Equals("picture", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("photo", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("pic", StringComparison.OrdinalIgnoreCase):
            case "has" when val.Equals("picture", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("photo", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("pic", StringComparison.OrdinalIgnoreCase):
                filter.HasImages = !isNegated;
                break;

            // Files / Documents
            case "isfile" or "file" or "attachment" or "attach" or "doc":
                filter.HasFiles = isNegated ? false : ParseBooleanOrTrue(val);
                break;

            case "is" when val.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("attachment", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("doc", StringComparison.OrdinalIgnoreCase):
            case "has" when val.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("attachment", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("doc", StringComparison.OrdinalIgnoreCase):
                filter.HasFiles = !isNegated;
                break;

            // Links / URLs
            case "has" when val.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("url", StringComparison.OrdinalIgnoreCase):
            case "link" or "url" or "web" or "website":
                filter.HasLinks = !isNegated;
                break;

            // Reactions
            case "has" when val.Equals("reaction", StringComparison.OrdinalIgnoreCase) ||
                            val.Equals("react", StringComparison.OrdinalIgnoreCase):
                filter.HasReactions = !isNegated;
                break;

            case "reaction" or "react":
                if (!string.IsNullOrWhiteSpace(val))
                {
                    filter.ReactionEmoji = val;
                    filter.HasReactions = !isNegated;
                }
                break;

            // Dates
            case "before" or "until" or "older" or "to_date":
                if (TryParseDate("before", val, out var beforeDate))
                    filter.Before = beforeDate;
                break;

            case "after" or "since" or "newer" or "from_date":
                if (TryParseDate("after", val, out var afterDate))
                    filter.After = afterDate;
                break;

            default:
                // Unknown tag - preserve as free text keyword if value is present
                if (!string.IsNullOrWhiteSpace(val))
                {
                    filter.TextTerms.Add($"{tag}:{val}");
                }
                break;
        }
    }

    private static bool ParseBooleanOrTrue(string val)
    {
        if (bool.TryParse(val, out var b)) return b;
        return true;
    }

    private static bool TryParseDate(string tag, string val, out DateTime date)
    {
        var lower = val.Trim().ToLowerInvariant();
        var today = DateTime.UtcNow.Date;
        bool isAfter = tag is "after" or "since" or "newer" or "from_date";

        if (lower == "today")
        {
            date = isAfter ? today : today.AddDays(1).AddTicks(-1); // Start or end of today
            return true;
        }
        if (lower == "yesterday")
        {
            date = isAfter ? today.AddDays(-1) : today.AddTicks(-1); // Start of yesterday or end of yesterday
            return true;
        }
        if (lower == "thisweek")
        {
            int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            date = today.AddDays(-1 * diff);
            return true;
        }
        if (lower == "lastweek")
        {
            int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            date = today.AddDays(-1 * diff - 7);
            return true;
        }

        if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out date) ||
            DateTime.TryParse(val, CultureInfo.CurrentCulture, DateTimeStyles.AdjustToUniversal, out date))
        {
            // For "before" filters with no time specified, match up to the end of that day (inclusive)
            if (!isAfter && date.TimeOfDay == TimeSpan.Zero)
            {
                date = date.Date.AddDays(1).AddTicks(-1);
            }
            return true;
        }

        return false;
    }
}
