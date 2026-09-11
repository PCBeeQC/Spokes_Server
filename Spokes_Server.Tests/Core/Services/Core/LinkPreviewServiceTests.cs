using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services;
using System.Collections.Generic;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class LinkPreviewServiceTests : IDisposable
    {
        private readonly LinkPreviewService _service;

        public LinkPreviewServiceTests()
        {
            _service = new LinkPreviewService();
        }

        public void Dispose()
        {
            _service.Dispose();
        }

        [Theory]
        [InlineData("Check this out: https://google.com", "https://google.com")]
        [InlineData("Multiple links: http://test.com and https://another.org/path", "http://test.com", "https://another.org/path")]
        [InlineData("With punctuation: https://spokes.app!", "https://spokes.app")]
        [InlineData("Inside brackets (https://github.com)", "https://github.com")]
        [InlineData("No links here", null)]
        public void ExtractUrls_ReturnsExpectedUrls(string content, params string?[] expected)
        {
            var result = _service.ExtractUrls(content);

            if (expected == null || expected[0] == null)
            {
                Assert.Empty(result);
            }
            else
            {
                Assert.Equal(expected.Length, result.Count);
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i], result[i]);
                }
            }
        }

        [Fact]
        public void ExtractUrls_LimitsToThree()
        {
            var content = "1: http://1.com 2: http://2.com 3: http://3.com 4: http://4.com";
            var result = _service.ExtractUrls(content);
            Assert.Equal(3, result.Count);
            Assert.Contains("http://1.com", result);
            Assert.Contains("http://3.com", result);
            Assert.DoesNotContain("http://4.com", result);
        }

        [Theory]
        [InlineData("https://www.google.com/search", "google.com")]
        [InlineData("http://spokes.app/chat", "spokes.app")]
        [InlineData("invalid-url", "invalid-url")]
        public void GetDomain_ReturnsCleanDomain(string url, string expected)
        {
            var result = LinkPreviewService.GetDomain(url);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetPreview_ReturnsNull_WhenNotCached()
        {
            var result = _service.GetPreview("https://never-seen-this.com");
            Assert.Null(result);
        }
    }
}

