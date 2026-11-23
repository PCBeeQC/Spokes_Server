using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Server-side sanitizer for inbound email HTML passed to the isolated print iframe.
/// Removes active script execution vectors, dangerous schemes, and (when remote images
/// are disallowed) external subresource references prior to iframe srcdoc injection.
/// </summary>
public static class EmailPrintSanitizer
{
    private static readonly Regex ExternalCssUrlRegex = new(
        @"url\s*\(\s*(['""]?)\s*(?:https?:)?//[^)\s]+\s*\1\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> DisallowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet",
        "form", "input", "button", "select", "textarea", "meta", "base", "link",
        "svg", "math", "portal", "audio", "video", "source", "track", "canvas"
    };

    private static readonly string[] AllowedDataImagePrefixes =
    [
        "data:image/png;",
        "data:image/jpeg;",
        "data:image/jpg;",
        "data:image/gif;",
        "data:image/webp;",
        "data:image/bmp;"
    ];

    public static string SanitizeForPrint(string? rawHtml, bool allowRemoteImages = false)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return string.Empty;
        }

        var sanitizer = CreateSanitizer(allowRemoteImages);
        return sanitizer.Sanitize(rawHtml);
    }

    private static HtmlSanitizer CreateSanitizer(bool allowRemoteImages)
    {
        var sanitizer = new HtmlSanitizer();

        foreach (var tag in DisallowedTags)
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        foreach (var attr in new[]
        {
            "class", "style", "align", "valign", "width", "height",
            "colspan", "rowspan", "border", "cellpadding", "cellspacing",
            "bgcolor", "color", "target", "rel"
        })
        {
            sanitizer.AllowedAttributes.Add(attr);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("data");

        sanitizer.FilterUrl += (_, args) => FilterUrl(args, allowRemoteImages);
        sanitizer.PostProcessNode += (_, args) =>
        {
            if (!allowRemoteImages && args.Node is IElement element)
            {
                var style = element.GetAttribute("style");
                if (!string.IsNullOrEmpty(style) && ExternalCssUrlRegex.IsMatch(style))
                {
                    var stripped = ExternalCssUrlRegex.Replace(style, "none");
                    element.SetAttribute("style", stripped);
                }
            }
        };

        return sanitizer;
    }

    private static void FilterUrl(FilterUrlEventArgs args, bool allowRemoteImages)
    {
        var url = args.OriginalUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            args.SanitizedUrl = null;
            return;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var isSafeImage = false;
            foreach (var prefix in AllowedDataImagePrefixes)
            {
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    isSafeImage = true;
                    break;
                }
            }

            if (!isSafeImage)
            {
                args.SanitizedUrl = null;
            }
            return;
        }

        if (!allowRemoteImages && IsRemoteHttpUrl(url))
        {
            // Keep explicit clickable links (<a href="https://...">) while stripping
            // subresource loads such as <img src>, background=, poster=, etc.
            var isAnchorHref = args.Tag != null &&
                               args.Tag.NodeName.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(args.Tag.GetAttribute("href"), args.OriginalUrl, StringComparison.Ordinal);
            if (!isAnchorHref)
            {
                args.SanitizedUrl = null;
            }
        }
    }

    private static bool IsRemoteHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("//", StringComparison.Ordinal);
}
