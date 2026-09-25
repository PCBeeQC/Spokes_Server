using System.Collections.Concurrent;
using System.Reflection;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

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

        [Fact]
        public void ExtractUrls_InsideCodeBlocks_AreIgnored()
        {
            // Test [code] tags
            var content1 = "[code]https://ignored.com[/code] and https://included.com";
            var result1 = _service.ExtractUrls(content1);
            Assert.Single(result1);
            Assert.Equal("https://included.com", result1[0]);

            // Test [code=language] tags
            var content2 = "[code=csharp]https://ignored2.com[/code] and https://included2.com";
            var result2 = _service.ExtractUrls(content2);
            Assert.Single(result2);
            Assert.Equal("https://included2.com", result2[0]);

            // Test markdown code blocks
            var content3 = "```csharp\nhttps://codeblock.com\n``` and https://valid.com";
            var result3 = _service.ExtractUrls(content3);
            Assert.Single(result3);
            Assert.Equal("https://valid.com", result3[0]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ExtractUrls_NullOrWhitespace_ReturnsEmpty(string? content)
        {
            var result = _service.ExtractUrls(content!);
            Assert.NotNull(result);
            Assert.Empty(result);
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

        [Fact]
        public void GetPreview_WhenCachedAndNotExpired_ReturnsCached()
        {
            var cacheField = typeof(LinkPreviewService).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cacheField);
            var cache = (ConcurrentDictionary<string, LinkPreview>?)cacheField.GetValue(_service);
            Assert.NotNull(cache);

            var preview = new LinkPreview
            {
                Url = "https://spokes.app/doc",
                Title = "Spokes Documentation",
                FetchedAt = DateTime.UtcNow,
                Failed = false
            };
            cache["https://spokes.app/doc"] = preview;

            var result = _service.GetPreview("https://spokes.app/doc");

            Assert.NotNull(result);
            Assert.Same(preview, result);
            Assert.Equal("Spokes Documentation", result.Title);
        }

        [Fact]
        public void GetPreview_WhenCachedAndExpired_RemovesFromCacheAndReturnsNull()
        {
            var cacheField = typeof(LinkPreviewService).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cacheField);
            var cache = (ConcurrentDictionary<string, LinkPreview>?)cacheField.GetValue(_service);
            Assert.NotNull(cache);

            var expiredPreview = new LinkPreview
            {
                Url = "https://spokes.app/expired",
                Title = "Expired Documentation",
                FetchedAt = DateTime.UtcNow.AddHours(-25),
                Failed = false
            };
            cache["https://spokes.app/expired"] = expiredPreview;

            var result = _service.GetPreview("https://spokes.app/expired");

            Assert.Null(result);
            Assert.False(cache.ContainsKey("https://spokes.app/expired"));
        }

        [Fact]
        public void GetPreview_WhenCachedFailed_ReturnsNull()
        {
            var cacheField = typeof(LinkPreviewService).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(cacheField);
            var cache = (ConcurrentDictionary<string, LinkPreview>?)cacheField.GetValue(_service);
            Assert.NotNull(cache);

            var failedPreview = new LinkPreview
            {
                Url = "https://spokes.app/failed",
                FetchedAt = DateTime.UtcNow,
                Failed = true
            };
            cache["https://spokes.app/failed"] = failedPreview;

            var result = _service.GetPreview("https://spokes.app/failed");

            Assert.Null(result);
        }

        private class FailingHttpMessageHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                throw new HttpRequestException("Simulated network failure");
            }
        }

        [Fact]
        public async Task FetchPreviewAsync_YouTubeUrl_DetectsVideoIdAndEmbedUrl()
        {
            var clientField = typeof(LinkPreviewService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(clientField);
            using var failingClient = new HttpClient(new FailingHttpMessageHandler());
            clientField.SetValue(_service, failingClient);

            var url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            var preview = await _service.FetchPreviewAsync(url);

            Assert.NotNull(preview);
            Assert.True(preview.IsYouTube);
            Assert.NotNull(preview.VideoEmbedUrl);
            Assert.Contains("dQw4w9WgXcQ", preview.VideoEmbedUrl);
            Assert.NotNull(preview.ImageUrl);
            Assert.Contains("dQw4w9WgXcQ", preview.ImageUrl);
            Assert.Equal("YouTube Video", preview.Title);
        }

        [Fact]
        public void Dispose_DisposesResourcesWithoutThrowing()
        {
            var service = new LinkPreviewService();
            service.Dispose();
            var ex = Record.Exception(() => service.Dispose());
            Assert.Null(ex);
        }
    }
