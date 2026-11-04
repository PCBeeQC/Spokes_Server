using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Ganss.Xss;
using Markdown.ColorCode;

namespace Spokes_Server.Core.Services.Content;

public class MarkdownSanitizerService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    // HTML5 void elements that do not require or allow a closing tag.
    private static readonly HashSet<string> VoidHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    // Known active/executable/structural HTML tags that should NOT be escaped as literal text,
    // so that HtmlSanitizer strips them (and their inner content for script/style) completely.
    private static readonly HashSet<string> DangerousHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "applet", "audio", "base", "bgsound", "blink", "body", "canvas", "dialog",
        "embed", "frame", "frameset", "head", "html", "iframe", "isindex", "keygen",
        "link", "marquee", "math", "meta", "nextid", "noembed", "noframes", "noscript",
        "object", "param", "plaintext", "portal", "script", "slot", "source", "style",
        "svg", "template", "title", "track", "video", "xmp"
    };

    private static readonly Regex HtmlTagPrefixRegex = new(
        @"^<\s*(/)?\s*([a-zA-Z][a-zA-Z0-9-]*)(?=[\s/>])",
        RegexOptions.Compiled);

    private static readonly Regex BlockHtmlTagRegex = new(
        @"<\s*(/)?\s*([a-zA-Z][a-zA-Z0-9-]*)(?=[\s/>])[^>]*>",
        RegexOptions.Compiled);

    public MarkdownSanitizerService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .UseColorCode(Markdown.ColorCode.HtmlFormatterType.Style, ColorCode.Styling.StyleDictionary.DefaultDark)
            .Use<LinkTargetExtension>()
            .Build();

        _sanitizer = new HtmlSanitizer();
        // Allow common attributes used by the app, such as 'class' for formatting/mentions and 'style' for layouts
        _sanitizer.AllowedAttributes.Add("class");
        _sanitizer.AllowedAttributes.Add("style");

        // Let's ensure target attributes are preserved for autolinks
        _sanitizer.AllowedAttributes.Add("target");
        _sanitizer.AllowedAttributes.Add("rel");
    }

    private static readonly ConcurrentDictionary<string, string> _htmlCache = new();
    private static readonly ConcurrentQueue<string> _cacheKeys = new();
    private const int MaxCacheSize = 5000;

    /// <summary>
    /// Parses Markdown and sanitizes the resulting HTML to prevent XSS.
    /// Used natively across the entire application for secure rendering.
    /// </summary>
    public string RenderSanitizedHtml(string rawMarkdown)
    {
        if (string.IsNullOrEmpty(rawMarkdown))
            return string.Empty;

        if (_htmlCache.TryGetValue(rawMarkdown, out var cachedHtml))
            return cachedHtml;

        // Escape double carets often used to point up in chat, preventing them from 
        // being swallowed by Markdig as empty superscripts or footer blocks.
        var processedMarkdown = rawMarkdown.Replace("^^", @"\^\^");

        // Pre-process blank lines to preserve WYSIWYG whitespace while keeping block boundaries intact
        processedMarkdown = PreserveBlankLines(processedMarkdown);

        // Parse into AST, escape non-HTML or unbalanced angle-bracket placeholders, and render HTML
        var document = Markdig.Markdown.Parse(processedMarkdown, _pipeline);
        EscapeInvalidHtmlNodes(document);
        var rawHtml = RenderDocumentToHtml(document);

        // Sanitize the HTML
        var sanitizedHtml = _sanitizer.Sanitize(rawHtml);

        // Cache the result
        if (_cacheKeys.Count >= MaxCacheSize)
        {
            if (_cacheKeys.TryDequeue(out var oldestKey))
            {
                _htmlCache.TryRemove(oldestKey, out _);
            }
        }

        _htmlCache[rawMarkdown] = sanitizedHtml;
        _cacheKeys.Enqueue(rawMarkdown);

        return sanitizedHtml;
    }

    private string RenderDocumentToHtml(MarkdownDocument document)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// Walks the parsed Markdig AST to escape raw angle-bracket tokens (<c>&lt;machine-id&gt;</c>,
    /// <c>&lt;id&gt;</c>, <c>&lt;wan-ip&gt;</c>, generic types <c>&lt;string&gt;</c>, or unclosed
    /// placeholders like <c>&lt;code&gt;</c> / <c>&lt;time&gt;</c>) so that AngleSharp/HtmlSanitizer
    /// does not treat them as unclosed HTML elements and strip the remainder of the document,
    /// while preserving balanced allowed HTML tags and letting HtmlSanitizer strip dangerous tags.
    /// </summary>
    private void EscapeInvalidHtmlNodes(MarkdownDocument document)
    {
        Stack<Block> blocks = new(document);
        while (blocks.Count > 0)
        {
            var block = blocks.Pop();

            if (block is HtmlBlock htmlBlock)
            {
                EscapeInvalidHtmlInBlock(htmlBlock);
            }
            else if (block is LeafBlock { Inline: not null } leafBlock)
            {
                EscapeInvalidHtmlInInlineTree(leafBlock.Inline);
            }
            else if (block is ContainerBlock container)
            {
                for (int i = container.Count - 1; i >= 0; i--)
                {
                    blocks.Push(container[i]);
                }
            }
        }
    }

    private void EscapeInvalidHtmlInInlineTree(ContainerInline rootInline)
    {
        List<HtmlInline> htmlInlines = [];
        CollectHtmlInlines(rootInline, htmlInlines);
        if (htmlInlines.Count == 0)
            return;

        Dictionary<string, Stack<HtmlInline>> openTagStacks = new(StringComparer.OrdinalIgnoreCase);
        HashSet<HtmlInline> toEscape = [];

        foreach (var htmlInline in htmlInlines)
        {
            var rawTag = htmlInline.Tag;
            if (string.IsNullOrEmpty(rawTag))
                continue;

            var trimmed = rawTag.Trim();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
                continue;

            var match = HtmlTagPrefixRegex.Match(trimmed);
            if (!match.Success)
            {
                toEscape.Add(htmlInline);
                continue;
            }

            bool isClosing = match.Groups[1].Success;
            string tagName = match.Groups[2].Value;
            bool isSelfClosing = !isClosing && trimmed.EndsWith("/>", StringComparison.Ordinal);

            // Leave dangerous HTML tags untouched so HtmlSanitizer removes the tag and its executable payload.
            if (DangerousHtmlTags.Contains(tagName))
                continue;

            // Unknown tag names (e.g. <machine-id>, <id>, <wan-ip>, <string>) are placeholders, not HTML.
            if (!_sanitizer.AllowedTags.Contains(tagName))
            {
                toEscape.Add(htmlInline);
                continue;
            }

            if (VoidHtmlTags.Contains(tagName) || isSelfClosing)
            {
                if (isClosing)
                {
                    toEscape.Add(htmlInline);
                }
                continue;
            }

            if (!isClosing)
            {
                if (!openTagStacks.TryGetValue(tagName, out var stack))
                {
                    stack = new Stack<HtmlInline>();
                    openTagStacks[tagName] = stack;
                }
                stack.Push(htmlInline);
            }
            else
            {
                if (openTagStacks.TryGetValue(tagName, out var stack) && stack.Count > 0)
                {
                    stack.Pop();
                }
                else
                {
                    toEscape.Add(htmlInline);
                }
            }
        }

        foreach (var stack in openTagStacks.Values)
        {
            while (stack.Count > 0)
            {
                toEscape.Add(stack.Pop());
            }
        }

        foreach (var htmlInline in toEscape)
        {
            htmlInline.Tag = WebUtility.HtmlEncode(htmlInline.Tag);
        }
    }

    private static void CollectHtmlInlines(ContainerInline container, List<HtmlInline> results)
    {
        var current = container.FirstChild;
        while (current != null)
        {
            if (current is HtmlInline htmlInline)
            {
                results.Add(htmlInline);
            }
            else if (current is ContainerInline childContainer)
            {
                CollectHtmlInlines(childContainer, results);
            }
            current = current.NextSibling;
        }
    }

    private void EscapeInvalidHtmlInBlock(HtmlBlock htmlBlock)
    {
        if (htmlBlock.Type is HtmlBlockType.Comment
            or HtmlBlockType.CData
            or HtmlBlockType.DocumentType
            or HtmlBlockType.ProcessingInstruction)
        {
            return;
        }

        var blockText = htmlBlock.Lines.ToString();
        if (string.IsNullOrEmpty(blockText))
            return;

        var matches = BlockHtmlTagRegex.Matches(blockText);
        if (matches.Count == 0)
            return;

        Dictionary<string, Stack<int>> openTagStacks = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> indicesToEscape = [];
        bool hasValidOrDangerousHtmlTag = false;

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            bool isClosing = match.Groups[1].Success;
            string tagName = match.Groups[2].Value;
            bool isSelfClosing = !isClosing && match.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal);

            if (DangerousHtmlTags.Contains(tagName))
            {
                hasValidOrDangerousHtmlTag = true;
                continue;
            }

            if (!_sanitizer.AllowedTags.Contains(tagName))
            {
                indicesToEscape.Add(i);
                continue;
            }

            if (VoidHtmlTags.Contains(tagName) || isSelfClosing)
            {
                if (isClosing)
                {
                    indicesToEscape.Add(i);
                }
                else
                {
                    hasValidOrDangerousHtmlTag = true;
                }
                continue;
            }

            if (!isClosing)
            {
                if (!openTagStacks.TryGetValue(tagName, out var stack))
                {
                    stack = new Stack<int>();
                    openTagStacks[tagName] = stack;
                }
                stack.Push(i);
            }
            else
            {
                if (openTagStacks.TryGetValue(tagName, out var stack) && stack.Count > 0)
                {
                    stack.Pop();
                    hasValidOrDangerousHtmlTag = true;
                }
                else
                {
                    indicesToEscape.Add(i);
                }
            }
        }

        foreach (var stack in openTagStacks.Values)
        {
            while (stack.Count > 0)
            {
                indicesToEscape.Add(stack.Pop());
            }
        }

        if (indicesToEscape.Count == 0)
            return;

        var sb = new StringBuilder(blockText.Length + indicesToEscape.Count * 8);
        int lastPos = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            sb.Append(blockText, lastPos, match.Index - lastPos);
            if (indicesToEscape.Contains(i))
            {
                sb.Append(WebUtility.HtmlEncode(match.Value));
            }
            else
            {
                sb.Append(match.Value);
            }
            lastPos = match.Index + match.Length;
        }
        sb.Append(blockText, lastPos, blockText.Length - lastPos);

        var rewritten = sb.ToString();
        if (!hasValidOrDangerousHtmlTag)
        {
            var normalized = rewritten.Replace("\r\n", "\n").Replace("\n", "<br />\n");
            rewritten = $"<p>{normalized}</p>";
        }

        htmlBlock.Lines = new StringLineGroup(rewritten);
    }

    private string PreserveBlankLines(string text)
    {
        // Parse the document to get block spans
        var document = Markdig.Markdown.Parse(text, _pipeline);
        
        List<SourceSpan> protectedSpans = [];
        
        Stack<Block> blocksToProcess = new(document);
        while (blocksToProcess.Count > 0)
        {
            var block = blocksToProcess.Pop();
            
            if (block is CodeBlock or FencedCodeBlock or HtmlBlock or ThematicBreakBlock)
            {
                protectedSpans.Add(block.Span);
            }
            else if (block is ContainerBlock container)
            {
                foreach (var child in container)
                {
                    blocksToProcess.Push(child);
                }
            }
        }

        string[] lines = text.Split('\n');
        bool[] isUnprotectedBlank = new bool[lines.Length];
        int currentIndex = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool isBlank = string.IsNullOrWhiteSpace(line);

            if (isBlank)
            {
                bool isProtected = false;
                if (protectedSpans.Count > 0 && protectedSpans.Any(s => currentIndex >= s.Start && currentIndex <= s.End))
                {
                    isProtected = true;
                }
                else if (i + 1 < lines.Length && IsThematicOrSetextDelimiter(lines[i + 1]))
                {
                    // Do not replace blank line preceding a thematic break/setext marker with &nbsp;
                    // to prevent CommonMark from parsing &nbsp;\n--- as a setext heading (<h2>).
                    isProtected = true;
                }
                else if (i > 0 && IsThematicOrSetextDelimiter(lines[i - 1]))
                {
                    isProtected = true;
                }

                isUnprotectedBlank[i] = !isProtected;
            }

            currentIndex += line.Length + 1;
        }

        var sb = new StringBuilder(text.Length + 100);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (isUnprotectedBlank[i])
            {
                bool hasCarriageReturn = line.EndsWith('\r');

                // Keep a true blank line before the first &nbsp; line in a run so preceding
                // paragraphs, lists, and blockquotes are properly closed in CommonMark.
                if (i > 0 && !string.IsNullOrWhiteSpace(lines[i - 1]))
                {
                    if (hasCarriageReturn) sb.Append('\r');
                    sb.Append('\n');
                }

                sb.Append("&nbsp;");
                if (hasCarriageReturn) sb.Append('\r');

                // Keep a true blank line after the last &nbsp; line in a run so the spacer
                // paragraph closes cleanly before the next Markdown block begins.
                if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                {
                    sb.Append('\n');
                    if (hasCarriageReturn) sb.Append('\r');
                }
            }
            else
            {
                sb.Append(line);
            }
            
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }
        
        return sb.ToString();
    }

    private static bool IsThematicOrSetextDelimiter(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length >= 3 && (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_')))
            return true;
        if (trimmed.Length >= 1 && trimmed.All(c => c == '='))
            return true;
        return false;
    }
}
