using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Moq;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class LinkTargetExtensionTests
    {
        private readonly MarkdownPipeline _pipeline;

        public LinkTargetExtensionTests()
        {
            _pipeline = new MarkdownPipelineBuilder()
                .Use<LinkTargetExtension>()
                .Build();
        }

        [Fact]
        public void Setup_WithMarkdownPipelineBuilder_DoesNotThrow()
        {
            // Arrange
            var extension = new LinkTargetExtension();
            var builder = new MarkdownPipelineBuilder();

            // Act
            var exception = Record.Exception(() => extension.Setup(builder));

            // Assert
            Assert.Null(exception);
        }

        [Fact]
        public void Setup_WithNonHtmlRenderer_ReturnsSafely()
        {
            // Arrange
            var extension = new LinkTargetExtension();
            var pipeline = new MarkdownPipelineBuilder().Build();
            var nonHtmlRenderer = Mock.Of<IMarkdownRenderer>();

            // Act
            var exception = Record.Exception(() => extension.Setup(pipeline, nonHtmlRenderer));

            // Assert
            Assert.Null(exception);
        }

        [Fact]
        public void Setup_WithHtmlRenderer_ReplacesRenderersSuccessfully()
        {
            // Arrange
            var extension = new LinkTargetExtension();
            var pipeline = new MarkdownPipelineBuilder().Build();
            using var writer = new StringWriter();
            var htmlRenderer = new HtmlRenderer(writer);

            // Act & Assert - first call removes default renderers and adds custom renderers
            var exception1 = Record.Exception(() => extension.Setup(pipeline, htmlRenderer));
            Assert.Null(exception1);

            // Act & Assert - second call handles case where default exact renderers are no longer present
            var exception2 = Record.Exception(() => extension.Setup(pipeline, htmlRenderer));
            Assert.Null(exception2);
        }

        [Fact]
        public void MarkdownLink_RendersWithTargetBlankAndRel()
        {
            // Arrange
            var markdown = "[Spokes](https://spokes.app)";

            // Act
            var html = Markdig.Markdown.ToHtml(markdown, _pipeline);

            // Assert
            Assert.Contains("target=\"_blank\"", html);
            Assert.Contains("rel=\"noopener noreferrer\"", html);
            Assert.Contains("href=\"https://spokes.app\"", html);
            Assert.Contains(">Spokes</a>", html);
        }

        [Fact]
        public void Autolink_RendersWithTargetBlankAndRel()
        {
            // Arrange
            var markdown = "<https://spokes.app>";

            // Act
            var html = Markdig.Markdown.ToHtml(markdown, _pipeline);

            // Assert
            Assert.Contains("target=\"_blank\"", html);
            Assert.Contains("rel=\"noopener noreferrer\"", html);
            Assert.Contains("href=\"https://spokes.app\"", html);
        }

        [Fact]
        public void Image_DoesNotAddTargetBlankOrRel()
        {
            // Arrange
            var markdown = "![Alt text](https://spokes.app/logo.png)";

            // Act
            var html = Markdig.Markdown.ToHtml(markdown, _pipeline);

            // Assert
            Assert.DoesNotContain("target=\"_blank\"", html);
            Assert.DoesNotContain("rel=\"noopener noreferrer\"", html);
            Assert.Contains("<img", html);
            Assert.Contains("src=\"https://spokes.app/logo.png\"", html);
        }

        [Fact]
        public void MultipleLinks_AllReceiveTargetAndRel()
        {
            // Arrange
            var markdown = "[Link 1](https://a.com) and [Link 2](https://b.com)";

            // Act
            var html = Markdig.Markdown.ToHtml(markdown, _pipeline);

            // Assert
            Assert.Contains("href=\"https://a.com\"", html);
            Assert.Contains("href=\"https://b.com\"", html);

            var targetMatches = Regex.Matches(html, Regex.Escape("target=\"_blank\"")).Count;
            var relMatches = Regex.Matches(html, Regex.Escape("rel=\"noopener noreferrer\"")).Count;

            Assert.Equal(2, targetMatches);
            Assert.Equal(2, relMatches);
        }
    }
