using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace Spokes_Server.Core.Services.Core;

/// <summary>
/// Markdig extension that adds target="_blank" and rel="noopener noreferrer"
/// to all rendered anchor tags, ensuring links open in a new browser tab.
/// </summary>
public class LinkTargetExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) { }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer htmlRenderer) return;

        // Replace the default LinkInline renderer with our custom one
        var defaultLinkRenderer = htmlRenderer.ObjectRenderers.FindExact<LinkInlineRenderer>();
        if (defaultLinkRenderer != null)
        {
            htmlRenderer.ObjectRenderers.Remove(defaultLinkRenderer);
        }
        htmlRenderer.ObjectRenderers.Add(new ExternalLinkInlineRenderer());

        // Replace the default AutolinkInline renderer with our custom one
        var defaultAutolinkRenderer = htmlRenderer.ObjectRenderers.FindExact<AutolinkInlineRenderer>();
        if (defaultAutolinkRenderer != null)
        {
            htmlRenderer.ObjectRenderers.Remove(defaultAutolinkRenderer);
        }
        htmlRenderer.ObjectRenderers.Add(new ExternalAutolinkInlineRenderer());
    }

    /// <summary>
    /// Custom renderer for LinkInline that injects target="_blank" on non-image links.
    /// </summary>
    private class ExternalLinkInlineRenderer : LinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            if (!link.IsImage && link.Url != null)
            {
                link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
                link.GetAttributes().AddPropertyIfNotExist("rel", "noopener noreferrer");
            }
            base.Write(renderer, link);
        }
    }

    /// <summary>
    /// Custom renderer for AutolinkInline that injects target="_blank".
    /// </summary>
    private class ExternalAutolinkInlineRenderer : AutolinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link)
        {
            link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
            link.GetAttributes().AddPropertyIfNotExist("rel", "noopener noreferrer");
            base.Write(renderer, link);
        }
    }
}
