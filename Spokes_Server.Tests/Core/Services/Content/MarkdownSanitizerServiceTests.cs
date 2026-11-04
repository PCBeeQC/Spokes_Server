
namespace Spokes_Server.Tests.Core.Services.Content;

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
            Assert.Equal(string.Empty, _service.RenderSanitizedHtml(null!));
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
            var codeSection = result[..codeEnd];
            Assert.DoesNotContain("&nbsp;", codeSection);

            // Assert: Blank line outside code block is preserved as &nbsp;
            var textSection = result[codeEnd..];
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

        [Fact]
        public void RenderSanitizedHtml_CachesResult_ReturnsSameOutput()
        {
            // Arrange
            var markdown = "Caching test markdown";

            // Act
            var firstResult = _service.RenderSanitizedHtml(markdown);
            var secondResult = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.NotNull(firstResult);
            Assert.Equal(firstResult, secondResult);
        }

        [Fact]
        public void RenderSanitizedHtml_ThematicDelimiters_AsterisksAndUnderscores()
        {
            // Arrange & Act
            var resultAsterisks = _service.RenderSanitizedHtml("Paragraph\n\n***\n\nNext");
            var resultUnderscores = _service.RenderSanitizedHtml("Paragraph\n\n___\n\nNext");

            // Assert
            Assert.Contains("<hr", resultAsterisks);
            Assert.DoesNotContain("<h2>&nbsp;</h2>", resultAsterisks);
            Assert.DoesNotContain("<h2", resultAsterisks);

            Assert.Contains("<hr", resultUnderscores);
            Assert.DoesNotContain("<h2>&nbsp;</h2>", resultUnderscores);
            Assert.DoesNotContain("<h2", resultUnderscores);
        }

        [Fact]
        public void RenderSanitizedHtml_SetextHeadingDelimiterWithEquals()
        {
            // Arrange
            var markdown = "Heading Title\n===\n\nParagraph text";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("<h1>Heading Title</h1>", result);
            Assert.Contains("<p>Paragraph text</p>", result);
        }

        [Fact]
        public void RenderSanitizedHtml_ContainerBlockquoteWithBlankLine()
        {
            // Arrange
            var markdown = "> Quote line 1\n>\n> Quote line 2";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("<blockquote>", result);
            Assert.Contains("Quote line 1", result);
            Assert.Contains("Quote line 2", result);
        }

        [Fact]
        public void RenderSanitizedHtml_PreservesCarriageReturns_WhenBlankLineHasCrLf()
        {
            // Arrange
            var markdown = "Line 1\r\n\r\nLine 2";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("&nbsp;", result);
            Assert.Contains("Line 1", result);
            Assert.Contains("Line 2", result);
        }

        [Fact]
        public void RenderSanitizedHtml_AngleBracketPlaceholdersInUrlsAndText_PreservesFullMessageAndPlaceholders()
        {
            // Arrange: Exact multi-paragraph message with <machine-id>, <id>, and <wan-ip> placeholders
            var markdown = """
                Voici la réponse de gemini à mes questions. Mon intuition était bonne, plex run effectivement leur propre genre de dns. J'ai appliquer le fix proposé, on va voir si ça règle le problème.

                Your intuition is spot on—Plex actually does run its own in-house dynamic DNS and connection broker system, and that is almost certainly why Plex breaks on IP rotation while your other Cloudflare-backed services keep working seamlessly.

                Here is what is happening under the hood, followed by how to fix it permanently.

                What is Going On?
                1. Plex’s "In-House DNS" (*.plex.direct and plex.tv)

                Unless you have explicitly forced Plex to only use your custom Cloudflare domain, Plex clients do not use your domain name at all.

                Instead, Plex Media Server registers its discovered IP addresses with plex.tv, and Plex provides a special wildcard DNS service (*.plex.direct) to give every server automatic HTTPS certificates. Rather than pointing a static hostname at a changing IP, Plex bakes your IP address directly into the hostname itself:

                Local URL: https://192-168-1-50.<machine-id>.plex.direct:32400
                Remote URL: https://203-0-113-42.<machine-id>.plex.direct:32400

                When your ISP rotates your IP, two things go wrong:

                The hostname itself becomes invalid: It isn't just a DNS record updating behind the scenes—the actual URL the Apple TV is talking to (https://old-ip.<id>.plex.direct) is now dead.
                Server + Client caching: First, Plex Media Server is slow to notice silent WAN IP changes compared to your Cloudflare DDNS container. Second, the Apple TV app caches the active connection URL in memory and persists the "last known good" URL to disk.
                Why you have to force-close and retry twice: When you reopen the app, it immediately tries the cached old-ip.plex.direct URL from disk and fails ("Server Unavailable"). Clicking Retry #1 forces the app to query plex.tv/api/resources in the background for a fresh list of server URLs. Clicking Retry #2 finally connects using the new new-ip.plex.direct URL.

                Solution 2: Force Plex to Use Your Cloudflare Domain (Fixes Both Local & Remote)

                Since you already have a Cloudflare DDNS container and other services working seamlessly (likely via a reverse proxy on Unraid like SWAG, Nginx Proxy Manager, Traefik, or Caddy), you can make Plex use your Cloudflare domain instead of plex.direct WAN IPs:

                Put Plex behind your Reverse Proxy (e.g., https://plex.yourdomain.com) with a short DNS TTL on Cloudflare (Cloudflare's default "Auto" TTL is ~60–300 seconds; also keep the DNS record Grey Cloud / DNS-Only so you don't violate Cloudflare's ToS or hit CDN throttling for video streaming).
                Add your domain to Plex:
                Go to Plex Settings → Network → Custom server access URLs.
                Enter: https://plex.yourdomain.com:443,http://192.168.1.XX:32400
                Disable Plex's built-in "Remote Access" (Settings → Remote Access → Disable Remote Access):
                This is the key step most people miss. As long as built-in Remote Access is enabled, Plex will keep publishing https://<wan-ip>.<id>.plex.direct:32400 to plex.tv, and the Apple TV app will keep choosing and caching that raw-IP URL over your custom domain.
                When you disable built-in Remote Access and set Custom server access URLs, Plex only publishes https://plex.yourdomain.com:443 to plex.tv. When your ISP rotates your IP, the URL in the Apple TV app never changes—your Cloudflare DDNS container updates the A record in seconds, and the Apple TV reconnects automatically just like all your other services.
                """;

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert: All angle-bracket placeholders are escaped and visible as text
            Assert.Contains("&lt;machine-id&gt;.plex.direct:32400", result);
            Assert.Contains("&lt;id&gt;.plex.direct", result);
            Assert.Contains("&lt;wan-ip&gt;.&lt;id&gt;.plex.direct:32400", result);

            // Assert: Subsequent paragraphs and the final sentence of the message are never truncated
            Assert.Contains("Remote URL:", result);
            Assert.Contains("Solution 2: Force Plex to Use Your Cloudflare Domain", result);
            Assert.Contains("just like all your other services.", result);

            // Assert: Ordered list closes after item 1 and does not swallow the following paragraphs
            var olCloseIndex = result.IndexOf("</ol>");
            var paragraphAfterListIndex = result.IndexOf("Unless you have explicitly forced Plex");
            Assert.True(olCloseIndex > 0 && olCloseIndex < paragraphAfterListIndex);
        }

        [Fact]
        public void RenderSanitizedHtml_UnclosedAllowedTagNamesAndGenerics_EscapesUnmatchedTagsAndKeepsBalancedHtml()
        {
            // Arrange: Unclosed placeholders that share names with HTML tags (<code>, <time>, <data>, <option>)
            // alongside generics (List<string>) and a balanced allowed tag (<span class="mention">@user</span>)
            var markdown = "Endpoint: https://api.example.com/<code>/<time>/<data> with <option> and List<string> for <span class=\"mention\">@user</span> and orphan </div>";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert
            Assert.Contains("&lt;code&gt;", result);
            Assert.Contains("&lt;time&gt;", result);
            Assert.Contains("&lt;data&gt;", result);
            Assert.Contains("&lt;option&gt;", result);
            Assert.Contains("List&lt;string&gt;", result);
            Assert.Contains("&lt;/div&gt;", result);
            Assert.Contains("<span class=\"mention\">@user</span>", result);
        }

        [Fact]
        public void RenderSanitizedHtml_BlockLevelPlaceholderAndBlockquoteTermination_ClosesBlocksCleanly()
        {
            // Arrange: Standalone placeholder line (CommonMark Type 7 HTML block candidate) and a blockquote followed by normal text
            var markdown = "> Quoted line\n\n<machine-id>\nis the identifier";

            // Act
            var result = _service.RenderSanitizedHtml(markdown);

            // Assert: Blockquote closes before the placeholder paragraph, and <machine-id> is escaped
            var quoteCloseIdx = result.IndexOf("</blockquote>");
            var placeholderIdx = result.IndexOf("&lt;machine-id&gt;");
            Assert.True(quoteCloseIdx > 0 && quoteCloseIdx < placeholderIdx);
            Assert.Contains("is the identifier", result);
        }
    }
