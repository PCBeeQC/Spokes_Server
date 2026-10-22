using System.Collections.Concurrent;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Services.Content;

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
            var preview = new LinkPreview
            {
                Url = "https://spokes.app/doc",
                Title = "Spokes Documentation",
                FetchedAt = DateTime.UtcNow,
                Failed = false
            };
            _service.Cache["https://spokes.app/doc"] = preview;

            var result = _service.GetPreview("https://spokes.app/doc");

            Assert.NotNull(result);
            Assert.Same(preview, result);
            Assert.Equal("Spokes Documentation", result.Title);
        }

        [Fact]
        public void GetPreview_WhenCachedAndExpired_RemovesFromCacheAndReturnsNull()
        {
            var expiredPreview = new LinkPreview
            {
                Url = "https://spokes.app/expired",
                Title = "Expired Documentation",
                FetchedAt = DateTime.UtcNow.AddHours(-25),
                Failed = false
            };
            _service.Cache["https://spokes.app/expired"] = expiredPreview;

            var result = _service.GetPreview("https://spokes.app/expired");

            Assert.Null(result);
            Assert.False(_service.Cache.ContainsKey("https://spokes.app/expired"));
        }

        [Fact]
        public void GetPreview_WhenCachedFailed_ReturnsNull()
        {
            var failedPreview = new LinkPreview
            {
                Url = "https://spokes.app/failed",
                FetchedAt = DateTime.UtcNow,
                Failed = true
            };
            _service.Cache["https://spokes.app/failed"] = failedPreview;

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
            using var failingClient = new HttpClient(new FailingHttpMessageHandler());
            using var service = new LinkPreviewService(failingClient);

            var url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            var preview = await service.FetchPreviewAsync(url);

            Assert.NotNull(preview);
            Assert.True(preview.IsYouTube);
            Assert.NotNull(preview.VideoEmbedUrl);
            Assert.Contains("dQw4w9WgXcQ", preview.VideoEmbedUrl);
            Assert.NotNull(preview.ImageUrl);
            Assert.Contains("dQw4w9WgXcQ", preview.ImageUrl);
            Assert.Equal("YouTube Video", preview.Title);
        }

        [Fact]
        public async Task FetchPreviewAsync_YouTubeUrl_ParsesOEmbedSuccessfully()
        {
            var oEmbedJson = @"{
                ""title"": ""Rick Astley - Never Gonna Give You Up (Official Video)"",
                ""author_name"": ""Rick Astley"",
                ""provider_name"": ""YouTube"",
                ""thumbnail_url"": ""https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg""
            }";

            var handler = new CustomHttpMessageHandler(req =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Content = new StringContent(oEmbedJson, System.Text.Encoding.UTF8, "application/json");
                return resp;
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var preview = await service.FetchPreviewAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

            Assert.NotNull(preview);
            Assert.True(preview.IsYouTube);
            Assert.Equal("Rick Astley - Never Gonna Give You Up (Official Video)", preview.Title);
            Assert.Equal("Rick Astley", preview.Description);
            Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", preview.ImageUrl);
            Assert.Equal("YouTube", preview.SiteName);
            Assert.Contains("dQw4w9WgXcQ", preview.VideoEmbedUrl);
            Assert.Equal("https://www.youtube.com/favicon.ico", preview.FaviconUrl);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/watch?feature=shared&v=dQw4w9WgXcQ&t=42", "dQw4w9WgXcQ")]
        [InlineData("https://youtu.be/dQw4w9WgXcQ?si=12345", "dQw4w9WgXcQ")]
        public async Task FetchPreviewAsync_YouTubeUrlFormats_ExtractsCorrectVideoId(string url, string expectedVideoId)
        {
            using var failingClient = new HttpClient(new FailingHttpMessageHandler());
            using var service = new LinkPreviewService(failingClient);

            var preview = await service.FetchPreviewAsync(url);

            Assert.NotNull(preview);
            Assert.True(preview.IsYouTube);
            Assert.Contains(expectedVideoId, preview.VideoEmbedUrl);
            Assert.Contains(expectedVideoId, preview.ImageUrl);
        }

        private class CustomHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public CustomHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }

        [Fact]
        public async Task FetchPreviewAsync_WithOpenGraphMetadata_ParsesCorrectlyUsingAngleSharp()
        {
            var html = @"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>Ignored Fallback Title</title>
                    <meta property=""og:title"" content=""Poly Robotics"" />
                    <meta property=""og:description"" content=""Innovative robotics solutions"" />
                    <meta property=""og:image"" content=""https://example.com/logo.png"" />
                    <meta property=""og:site_name"" content=""PolyRobotics"" />
                </head>
                <body>Hello</body>
                </html>";

            var handler = new CustomHttpMessageHandler(req =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
                return resp;
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var preview = await service.FetchPreviewAsync("https://example.com/about");

            Assert.NotNull(preview);
            Assert.False(preview.Failed);
            Assert.Equal("Poly Robotics", preview.Title);
            Assert.Equal("Innovative robotics solutions", preview.Description);
            Assert.Equal("https://example.com/logo.png", preview.ImageUrl);
            Assert.Equal("PolyRobotics", preview.SiteName);
        }

        [Fact]
        public async Task FetchPreviewAsync_WithoutOpenGraph_FallsBackToTitleAndDescriptionMeta()
        {
            var html = @"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>Standard Page Title</title>
                    <meta name=""description"" content=""Standard meta description"" />
                </head>
                <body>Content</body>
                </html>";

            var handler = new CustomHttpMessageHandler(req =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
                return resp;
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var preview = await service.FetchPreviewAsync("https://example.com/standard");

            Assert.NotNull(preview);
            Assert.False(preview.Failed);
            Assert.Equal("Standard Page Title", preview.Title);
            Assert.Equal("Standard meta description", preview.Description);
        }

        [Fact]
        public async Task FetchPreviewAsync_WhenHtmlHasNoImage_DoesNotOverwriteExistingImageWithNull()
        {
            var html = @"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>No Image Title</title>
                    <meta name=""description"" content=""Description without image"" />
                </head>
                <body>Content</body>
                </html>";

            var handler = new CustomHttpMessageHandler(req =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                resp.Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
                return resp;
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var preview = await service.FetchPreviewAsync("https://example.com/no-image");

            Assert.NotNull(preview);
            Assert.False(preview.Failed);
            Assert.Equal("No Image Title", preview.Title);
            Assert.Equal("Description without image", preview.Description);
        }

        [Fact]
        public void Dispose_DisposesResourcesWithoutThrowing()
        {
            var service = new LinkPreviewService();
            service.Dispose();
            var ex = Record.Exception(() => service.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public async Task FetchPreviewAsync_WhenCancelled_ReturnsNullAndDoesNotCacheFailure()
        {
            using var cts = new System.Threading.CancellationTokenSource();
            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            var handler = new AsyncCustomHttpMessageHandler(async (req, ct) =>
            {
                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task;
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var fetchTask = service.FetchPreviewAsync("https://example.com/slow", cts.Token);
            cts.Cancel();

            var result = await fetchTask;
            Assert.Null(result);
            Assert.False(service.Cache.ContainsKey("https://example.com/slow"));
        }

        [Fact]
        public async Task FetchPreviewAsync_ConcurrentSameDomainRequests_DeduplicatesInFlightUrlAndFaviconHead()
        {
            var html1 = @"<!DOCTYPE html><html><head><title>Article 1</title><link rel=""icon"" href=""https://www.lapresse.ca/favicon-16x16.png"" /></head><body>1</body></html>";
            var html2 = @"<!DOCTYPE html><html><head><title>Article 2</title><link rel=""icon"" href=""https://www.lapresse.ca/favicon-16x16.png"" /></head><body>2</body></html>";

            int getArticle1Count = 0;
            int headFaviconCount = 0;

            var handler = new AsyncCustomHttpMessageHandler(async (req, ct) =>
            {
                await Task.Delay(25, ct);
                if (req.Method == HttpMethod.Head && req.RequestUri?.ToString() == "https://www.lapresse.ca/favicon-16x16.png")
                {
                    System.Threading.Interlocked.Increment(ref headFaviconCount);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }

                if (req.RequestUri?.ToString() == "https://lp.ca/article1")
                {
                    System.Threading.Interlocked.Increment(ref getArticle1Count);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(html1, System.Text.Encoding.UTF8, "text/html")
                    };
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(html2, System.Text.Encoding.UTF8, "text/html")
                };
            });

            using var testClient = new HttpClient(handler);
            using var service = new LinkPreviewService(testClient);

            var results = await Task.WhenAll(
                service.FetchPreviewAsync("https://lp.ca/article1"),
                service.FetchPreviewAsync("https://lp.ca/article1"),
                service.FetchPreviewAsync("https://lp.ca/article2"));

            Assert.All(results, Assert.NotNull);
            Assert.Equal(1, getArticle1Count);
            Assert.Equal(1, headFaviconCount);
        }

        private class AsyncCustomHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> _responder;

            public AsyncCustomHttpMessageHandler(Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return _responder(request, cancellationToken);
            }
        }
    }
