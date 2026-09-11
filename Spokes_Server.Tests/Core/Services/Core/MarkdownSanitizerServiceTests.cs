using System;
using Xunit;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class MarkdownSanitizerServiceTests
    {
        private readonly MarkdownSanitizerService _service;

        public MarkdownSanitizerServiceTests()
        {
            _service = new MarkdownSanitizerService();
        }

        [Fact]
        public void RenderSanitizedHtml_NullOrEmpty_ReturnsEmptyString()
        {
            // Act & Assert
            Assert.Equal(string.Empty, _service.RenderSanitizedHtml(null));
            Assert.Equal(string.Empty, _service.RenderSanitizedHtml(""));
        }

        [Fact]
        public void RenderSanitizedHtml_ValidMarkdown_ConvertsToHtml()
        {
            // Arrange
            var markdown = "Hello **world**!";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("<p>Hello <strong>world</strong>!</p>", result);
        }

        [Fact]
        public void RenderSanitizedHtml_DoubleCarets_ArePreserved()
        {
            // Arrange
            var markdown = "Check this out ^^";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("^^", result);
        }

        [Fact]
        public void RenderSanitizedHtml_XssAttempt_IsSanitized()
        {
            // Arrange
            var markdown = "Dangerous <script>alert('hack')</script> and <img src=x onerror=alert(1)> text.";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.DoesNotContain("<script>", result);
            Assert.DoesNotContain("onerror", result);
            Assert.Contains("Dangerous", result);
            Assert.Contains("<img src=\"x\">", result);
        }

        [Fact]
        public void RenderSanitizedHtml_AllowedAttributes_ArePreserved()
        {
            // Arrange
            var markdown = "<span class=\"mention\" style=\"color: red;\">@user</span>";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("class=\"mention\"", result);
            Assert.Contains("style=", result);
            Assert.Contains("color", result);
        }

        [Fact]
        public void RenderSanitizedHtml_LinkAttributes_ArePreserved()
        {
            // Arrange
            var markdown = "<a href=\"https://google.com\" target=\"_blank\" rel=\"noopener noreferrer\">Link</a>";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("target=\"_blank\"", result);
            Assert.Contains("rel=\"noopener noreferrer\"", result);
        }

        [Fact]
        public void RenderSanitizedHtml_FencedCodeBlockWithBlankLines_PreservesCodeWithoutNbsp()
        {
            // Arrange
            var markdown = "```csharp\r\nvar a = 1;\r\n\r\nvar b = 2;\r\n```\r\n\r\nFollow up text";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert: Code block content does not contain &nbsp;
            var codeEnd = result.IndexOf("</pre>");
            Assert.True(codeEnd > 0);
            var codeSection = result.Substring(0, codeEnd);
            Assert.DoesNotContain("&nbsp;", codeSection);

            // Assert: Blank line outside code block is preserved as &nbsp;
            var textSection = result.Substring(codeEnd);
            Assert.Contains("&nbsp;", textSection);
        }

        [Fact]
        public void RenderSanitizedHtml_ThematicBreakWithBlankLines_RendersHrWithoutSetextHeading()
        {
            // Arrange
            var markdown = "Paragraph one\r\n\r\n---\r\n\r\nParagraph two";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert: Renders <hr /> and not <h2>&nbsp;</h2>
            Assert.Contains("<hr", result);
            Assert.DoesNotContain("<h2", result);
        }

        [Fact]
        public void RenderSanitizedHtml_PreservesBlankLinesAsNbsp()
        {
            // Arrange
            var markdown = "Line 1\n\nLine 2";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("&nbsp;", result);
        }
    }
}
