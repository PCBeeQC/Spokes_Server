using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Ganss.Xss;
using Markdown.ColorCode;

namespace Spokes_Server.Core.Services.Core;

public class MarkdownSanitizerService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _htmlCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _cacheKeys = new();
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

        // Pre-process blank lines to preserve WYSIWYG whitespace
        processedMarkdown = PreserveBlankLines(processedMarkdown);

        // Convert the Markdown to HTML
        var rawHtml = Markdig.Markdown.ToHtml(processedMarkdown, _pipeline);

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

    private string PreserveBlankLines(string text)
    {
        // Parse the document to get block spans
        var document = Markdig.Markdown.Parse(text, _pipeline);
        
        var protectedSpans = new System.Collections.Generic.List<SourceSpan>();
        
        var blocksToProcess = new System.Collections.Generic.Stack<Block>(document);
        while (blocksToProcess.Count > 0)
        {
            var block = blocksToProcess.Pop();
            
            if (block is CodeBlock || 
                block is FencedCodeBlock || 
                block is HtmlBlock ||
                block is ThematicBreakBlock)
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

        var sb = new StringBuilder(text.Length + 100);
        string[] lines = text.Split('\n');
        int currentIndex = 0;
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool isBlank = string.IsNullOrWhiteSpace(line);
            
            bool isProtected = false;
            if (isBlank)
            {
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
            }
            
            if (isBlank && !isProtected)
            {
                bool hasCarriageReturn = line.EndsWith("\r");
                sb.Append("&nbsp;");
                if (hasCarriageReturn) sb.Append('\r');
            }
            else
            {
                sb.Append(line);
            }
            
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
            
            currentIndex += line.Length + 1; 
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
