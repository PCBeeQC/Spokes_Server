using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Documents;

namespace Spokes_Server.Tests.Core.Services.Documents;

public class DocumentPdfServiceTests : IDisposable
{
    private readonly string _testTempDir;

    public DocumentPdfServiceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"Spokes_PdfTests_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempDir))
        {
            try
            {
                Directory.Delete(_testTempDir, true);
            }
            catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
        }
    }

    private DocumentPdfService CreateService(string? dataPath = null, ILogger<DocumentPdfService>? logger = null, IBrowserManager? browserManager = null)
    {
        var tokenService = new RenderTokenService();
        var httpContextAccessor = new Mock<IHttpContextAccessor>().Object;
        var inMemoryConfig = new Dictionary<string, string?>();
        if (dataPath != null)
        {
            inMemoryConfig["DataPath"] = dataPath;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
        var log = logger ?? new Mock<ILogger<DocumentPdfService>>().Object;
        var bm = browserManager ?? new Mock<IBrowserManager>().Object;

        return new DocumentPdfService(tokenService, httpContextAccessor, config, log, bm);
    }

    private (DocumentPdfService Service, Mock<IBrowser> MockBrowser, Mock<IPage> MockPage, Mock<ILogger<DocumentPdfService>> MockLogger) CreateServiceWithMockBrowser(string? dataPath = null)
    {
        var mockLogger = new Mock<ILogger<DocumentPdfService>>();
        var mockBrowser = new Mock<IBrowser>();
        var mockPage = new Mock<IPage>();

        mockBrowser.Setup(b => b.IsClosed).Returns(false);
        mockBrowser.Setup(b => b.NewPageAsync(It.IsAny<CreatePageOptions>())).ReturnsAsync(mockPage.Object);
        mockPage.Setup(p => p.SetExtraHttpHeadersAsync(It.IsAny<Dictionary<string, string>>())).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.EmulateMediaTypeAsync(MediaType.Print)).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.GoToAsync(It.IsAny<string>(), It.IsAny<NavigationOptions>())).Returns(Task.FromResult<IResponse?>(null));
        mockPage.Setup(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>())).Returns(Task.CompletedTask);
        mockPage.As<IAsyncDisposable>().Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockBrowserManager = new Mock<IBrowserManager>();
        mockBrowserManager.Setup(bm => bm.GetBrowserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockBrowser.Object);

        var service = CreateService(dataPath ?? _testTempDir, mockLogger.Object, mockBrowserManager.Object);
        service.Browser = mockBrowser.Object;

        return (service, mockBrowser, mockPage, mockLogger);
    }

    [Fact]
    public void Constructor_WithDefaultDataPath_UsesDataDirectory()
    {
        // Arrange & Act
        using var service = CreateService(dataPath: null);

        // Assert
        Assert.Equal("Data", service.DataPath);
    }

    [Fact]
    public void Constructor_WithCustomDataPath_UsesCustomDirectory()
    {
        // Arrange
        var customPath = @"C:\CustomDataPath\Spokes";

        // Act
        using var service = CreateService(dataPath: customPath);

        // Assert
        Assert.Equal(customPath, service.DataPath);
    }

    [Fact]
    public void Dispose_WhenBrowserIsNull_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        var exception = Record.Exception(() => service.Dispose());
        Assert.Null(exception);

        // Idempotent check
        var secondException = Record.Exception(() => service.Dispose());
        Assert.Null(secondException);
    }

    [Fact]
    public void Dispose_WhenBrowserIsSet_DisposesBrowserAndSetsNull()
    {
        // Arrange
        var (service, mockBrowser, _, _) = CreateServiceWithMockBrowser();

        // Act
        service.Dispose();

        // Assert
        mockBrowser.Verify(b => b.Dispose(), Times.Once);

        Assert.Null(service.Browser);

        // Disposing again should not call Dispose() on the mock again
        service.Dispose();
        mockBrowser.Verify(b => b.Dispose(), Times.Once);
    }

    [Fact]
    public async Task GenerateQuotePdfAsync_ConstructsCorrectUrlAndDestinationPath()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var quote = new Quote { Id = "quote-123" };
        var projectId = "proj-1";
        var baseUrl = "http://localhost:5000";

        // Act
        var result = await service.GenerateQuotePdfAsync(quote, projectId, baseUrl);

        // Assert
        var expectedRelativePath = Path.Combine(_testTempDir, "PDFs", "Quotes", "quote-123.pdf");
        var expectedFullPath = Path.GetFullPath(expectedRelativePath);
        Assert.Equal(expectedFullPath, result);

        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/projects/proj-1/quotes/quote-123?renderToken=")
                                && url.Length > "http://localhost:5000/projects/proj-1/quotes/quote-123?renderToken=".Length),
            It.Is<NavigationOptions>(opt => opt.WaitUntil != null && opt.WaitUntil[0] == WaitUntilNavigation.Networkidle0)),
            Times.Once);

        mockPage.Verify(p => p.PdfAsync(
            expectedFullPath,
            It.Is<PdfOptions>(opt => opt.PreferCSSPageSize && opt.PrintBackground)),
            Times.Once);

        // Verify directory was created
        var dir = Path.GetDirectoryName(expectedFullPath);
        Assert.NotNull(dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task GenerateInvoicePdfAsync_ConstructsCorrectUrlAndDestinationPath()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var invoice = new Invoice { Id = "inv-456" };
        var projectId = "proj-2";
        var baseUrl = "http://localhost:5000";

        // Act
        var result = await service.GenerateInvoicePdfAsync(invoice, projectId, baseUrl);

        // Assert
        var expectedRelativePath = Path.Combine(_testTempDir, "PDFs", "Invoices", "inv-456.pdf");
        var expectedFullPath = Path.GetFullPath(expectedRelativePath);
        Assert.Equal(expectedFullPath, result);

        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/projects/proj-2/invoices/inv-456?renderToken=")),
            It.IsAny<NavigationOptions>()),
            Times.Once);

        mockPage.Verify(p => p.PdfAsync(
            expectedFullPath,
            It.IsAny<PdfOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateProjectDocumentPdfAsync_ConstructsCorrectUrlAndDestinationPath()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var doc = new ProjectDocument { Id = "doc-789", ProjectId = "proj-3" };
        var projectId = "proj-3";
        var baseUrl = "http://localhost:5000";

        // Act
        var result = await service.GenerateProjectDocumentPdfAsync(doc, projectId, baseUrl);

        // Assert
        var expectedRelativePath = Path.Combine(_testTempDir, "PDFs", "ProjectDocuments", "doc-789.pdf");
        var expectedFullPath = Path.GetFullPath(expectedRelativePath);
        Assert.Equal(expectedFullPath, result);

        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/projects/proj-3/documents/doc-789?renderToken=")),
            It.IsAny<NavigationOptions>()),
            Times.Once);

        mockPage.Verify(p => p.PdfAsync(
            expectedFullPath,
            It.IsAny<PdfOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateBusinessDocumentPdfAsync_ConstructsCorrectUrlAndDestinationPath()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var doc = new ProjectDocument { Id = "bdoc-101" };
        var baseUrl = "http://localhost:5000";

        // Act
        var result = await service.GenerateBusinessDocumentPdfAsync(doc, baseUrl);

        // Assert
        var expectedRelativePath = Path.Combine(_testTempDir, "PDFs", "BusinessDocuments", "bdoc-101.pdf");
        var expectedFullPath = Path.GetFullPath(expectedRelativePath);
        Assert.Equal(expectedFullPath, result);

        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/business/documents/bdoc-101?renderToken=")),
            It.IsAny<NavigationOptions>()),
            Times.Once);

        mockPage.Verify(p => p.PdfAsync(
            expectedFullPath,
            It.IsAny<PdfOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task GeneratePurchaseOrderPdfAsync_ConstructsCorrectUrlAndDestinationPath()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var po = new PurchaseOrder { Id = "po-202" };
        var baseUrl = "http://localhost:5000";

        // Act
        var result = await service.GeneratePurchaseOrderPdfAsync(po, baseUrl);

        // Assert
        var expectedRelativePath = Path.Combine(_testTempDir, "PDFs", "PurchaseOrders", "po-202.pdf");
        var expectedFullPath = Path.GetFullPath(expectedRelativePath);
        Assert.Equal(expectedFullPath, result);

        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/purchases/po-202?renderToken=")),
            It.IsAny<NavigationOptions>()),
            Times.Once);

        mockPage.Verify(p => p.PdfAsync(
            expectedFullPath,
            It.IsAny<PdfOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task GeneratePdfAsync_WhenNavigationFails_LogsErrorAndRethrows()
    {
        // Arrange
        var (service, mockBrowser, mockPage, mockLogger) = CreateServiceWithMockBrowser();
        var quote = new Quote { Id = "quote-err" };

        mockPage
            .Setup(p => p.GoToAsync(It.IsAny<string>(), It.IsAny<NavigationOptions>()))
            .ThrowsAsync(new InvalidOperationException("Navigation timeout"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateQuotePdfAsync(quote, "proj-err", "http://localhost:5000"));

        Assert.Equal("Navigation timeout", ex.Message);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.Is<Exception>(e => e == ex),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateQuotePdfAsync_WithTrailingSlashBaseUrl_NormalizesWithoutDoubleSlash()
    {
        // Arrange
        var (service, _, mockPage, _) = CreateServiceWithMockBrowser();
        var quote = new Quote { Id = "quote-trailing" };
        var baseUrl = "http://localhost:5000/";

        // Act
        await service.GenerateQuotePdfAsync(quote, "proj-1", baseUrl);

        // Assert
        mockPage.Verify(p => p.GoToAsync(
            It.Is<string>(url => url.StartsWith("http://localhost:5000/projects/proj-1/quotes/quote-trailing?renderToken=")
                                && !url.Contains("5000//")),
            It.IsAny<NavigationOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task GeneratePdfAsync_EmulatesPrintMediaType_AndUsesCorrectMedia()
    {
        // Arrange
        var (service, _, mockPage, _) = CreateServiceWithMockBrowser();
        var quote = new Quote { Id = "quote-print" };

        // Act
        await service.GenerateQuotePdfAsync(quote, "proj-1", "http://localhost:5000");

        // Assert
        mockPage.Verify(p => p.EmulateMediaTypeAsync(MediaType.Print), Times.Once);
    }

    [Fact]
    public async Task GeneratePdfAsync_WhenCalledMultipleTimes_ReusesExistingBrowser()
    {
        // Arrange
        var (service, mockBrowser, mockPage, _) = CreateServiceWithMockBrowser();
        var quote1 = new Quote { Id = "quote-m1" };
        var quote2 = new Quote { Id = "quote-m2" };

        // Act
        await service.GenerateQuotePdfAsync(quote1, "proj-1", "http://localhost:5000");
        await service.GenerateQuotePdfAsync(quote2, "proj-1", "http://localhost:5000");

        // Assert - NewPageAsync should have been called twice on the same browser
        mockBrowser.Verify(b => b.NewPageAsync(It.IsAny<CreatePageOptions>()), Times.Exactly(2));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenBrowserManagerIsNull()
    {
        var tokenService = new RenderTokenService();
        var httpContextAccessor = new Mock<IHttpContextAccessor>().Object;
        var config = new ConfigurationBuilder().Build();
        var log = new Mock<ILogger<DocumentPdfService>>().Object;

        Assert.Throws<ArgumentNullException>(() =>
            new DocumentPdfService(tokenService, httpContextAccessor, config, log, null!));
    }

    [Fact]
    public async Task GeneratePdfAsync_DelegatesToBrowserManager_WhenBrowserNotExplicitlySet()
    {
        var mockBrowser = new Mock<IBrowser>();
        var mockPage = new Mock<IPage>();
        mockBrowser.Setup(b => b.IsClosed).Returns(false);
        mockBrowser.Setup(b => b.NewPageAsync(It.IsAny<CreatePageOptions>())).ReturnsAsync(mockPage.Object);
        mockPage.Setup(p => p.EmulateMediaTypeAsync(MediaType.Print)).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.GoToAsync(It.IsAny<string>(), It.IsAny<NavigationOptions>())).Returns(Task.FromResult<IResponse?>(null));
        mockPage.Setup(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>())).Returns(Task.CompletedTask);
        mockPage.As<IAsyncDisposable>().Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockBrowserManager = new Mock<IBrowserManager>();
        mockBrowserManager.Setup(bm => bm.GetBrowserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockBrowser.Object);

        var service = CreateService(dataPath: _testTempDir, browserManager: mockBrowserManager.Object);
        var quote = new Quote { Id = "quote-bm" };

        await service.GenerateQuotePdfAsync(quote, "proj-bm", "http://localhost:5000");

        mockBrowserManager.Verify(bm => bm.GetBrowserAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockPage.Verify(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>()), Times.Once);
    }

    [Theory]
    [InlineData(new[] { "http://+:8080" }, "http://127.0.0.1:8080")]
    [InlineData(new[] { "http://[::]:8080" }, "http://127.0.0.1:8080")]
    [InlineData(new[] { "http://0.0.0.0:5005" }, "http://127.0.0.1:5005")]
    [InlineData(new[] { "https://+:8443", "http://+:8080" }, "http://127.0.0.1:8080")]
    [InlineData(new[] { "https://+:8443" }, "https://127.0.0.1:8443")]
    public void TryResolveLoopbackAddress_ResolvesKestrelBindingsToLocalLoopback(string[] addresses, string expected)
    {
        var resolved = DocumentPdfService.TryResolveLoopbackAddress(addresses);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task GenerateInvoicePdfAsync_WhenRedirectedToAccessDenied_ThrowsAndDoesNotWritePdf()
    {
        // Arrange
        var (service, _, mockPage, _) = CreateServiceWithMockBrowser();
        var invoice = new Invoice { Id = "inv-denied" };
        mockPage.Setup(p => p.Url).Returns("https://mpr.poly-robotique.com/access-denied");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateInvoicePdfAsync(invoice, "proj-1", "https://mpr.poly-robotique.com"));

        Assert.Contains("/access-denied", ex.Message);
        mockPage.Verify(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>()), Times.Never);
    }

    [Fact]
    public async Task GenerateInvoicePdfAsync_RevokesRenderTokenInFinallyBlock()
    {
        // Arrange
        var tokenService = new RenderTokenService();
        var httpContextAccessor = new Mock<IHttpContextAccessor>().Object;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testTempDir })
            .Build();
        var mockLogger = new Mock<ILogger<DocumentPdfService>>();
        var mockBrowser = new Mock<IBrowser>();
        var mockPage = new Mock<IPage>();
        mockBrowser.Setup(b => b.IsClosed).Returns(false);
        mockBrowser.Setup(b => b.NewPageAsync(It.IsAny<CreatePageOptions>())).ReturnsAsync(mockPage.Object);
        mockPage.Setup(p => p.SetExtraHttpHeadersAsync(It.IsAny<Dictionary<string, string>>())).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.EmulateMediaTypeAsync(MediaType.Print)).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.GoToAsync(It.IsAny<string>(), It.IsAny<NavigationOptions>())).Returns(Task.FromResult<IResponse?>(null));
        mockPage.Setup(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>())).Returns(Task.CompletedTask);
        mockPage.As<IAsyncDisposable>().Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockBrowserManager = new Mock<IBrowserManager>();
        mockBrowserManager.Setup(bm => bm.GetBrowserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockBrowser.Object);

        using var service = new DocumentPdfService(tokenService, httpContextAccessor, config, mockLogger.Object, mockBrowserManager.Object);

        // Act
        await service.GenerateInvoicePdfAsync(new Invoice { Id = "inv-revoke" }, "proj-1", "http://localhost:5000");

        // Assert: Token was revoked upon completion
        Assert.Equal(0, tokenService.TokenCount);
    }

    [Fact]
    public void IsValidCachedPdf_DetectsAndDeletesCorruptedAccessDeniedPdf_AndAcceptsValidPdf()
    {
        // Arrange
        using var service = CreateService(_testTempDir);
        Directory.CreateDirectory(_testTempDir);

        var corruptedPath = Path.Combine(_testTempDir, "corrupted.pdf");
        File.WriteAllBytes(corruptedPath, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n/Title (mpr.poly-robotique.com/access-denied)\n%%EOF"));

        var validPath = Path.Combine(_testTempDir, "valid.pdf");
        File.WriteAllBytes(validPath, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n/Title (Invoice INV-2026-001)\n%%EOF"));

        // Act & Assert
        Assert.False(service.IsValidCachedPdf(corruptedPath));
        Assert.False(File.Exists(corruptedPath));

        Assert.True(service.IsValidCachedPdf(validPath));
        Assert.True(File.Exists(validPath));
    }
}
