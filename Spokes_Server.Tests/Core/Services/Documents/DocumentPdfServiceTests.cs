using System.Reflection;
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
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private DocumentPdfService CreateService(string? dataPath = null, ILogger<DocumentPdfService>? logger = null)
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

        return new DocumentPdfService(tokenService, httpContextAccessor, config, log);
    }

    private (DocumentPdfService Service, Mock<IBrowser> MockBrowser, Mock<IPage> MockPage, Mock<ILogger<DocumentPdfService>> MockLogger) CreateServiceWithMockBrowser(string? dataPath = null)
    {
        var mockLogger = new Mock<ILogger<DocumentPdfService>>();
        var service = CreateService(dataPath ?? _testTempDir, mockLogger.Object);

        var mockBrowser = new Mock<IBrowser>();
        var mockPage = new Mock<IPage>();

        mockBrowser.Setup(b => b.IsClosed).Returns(false);
        mockBrowser.Setup(b => b.NewPageAsync(It.IsAny<CreatePageOptions>())).ReturnsAsync(mockPage.Object);
        mockPage.Setup(p => p.EmulateMediaTypeAsync(MediaType.Print)).Returns(Task.CompletedTask);
        mockPage.Setup(p => p.GoToAsync(It.IsAny<string>(), It.IsAny<NavigationOptions>())).Returns(Task.FromResult<IResponse?>(null));
        mockPage.Setup(p => p.PdfAsync(It.IsAny<string>(), It.IsAny<PdfOptions>())).Returns(Task.CompletedTask);
        mockPage.As<IAsyncDisposable>().Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        typeof(DocumentPdfService).GetField("_browser", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, mockBrowser.Object);

        return (service, mockBrowser, mockPage, mockLogger);
    }

    [Fact]
    public void Constructor_WithDefaultDataPath_UsesDataDirectory()
    {
        // Arrange & Act
        using var service = CreateService(dataPath: null);

        // Assert
        var dataPathField = (string?)typeof(DocumentPdfService)
            .GetField("_dataPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

        Assert.Equal("Data", dataPathField);
    }

    [Fact]
    public void Constructor_WithCustomDataPath_UsesCustomDirectory()
    {
        // Arrange
        var customPath = @"C:\CustomDataPath\Spokes";

        // Act
        using var service = CreateService(dataPath: customPath);

        // Assert
        var dataPathField = (string?)typeof(DocumentPdfService)
            .GetField("_dataPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

        Assert.Equal(customPath, dataPathField);
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

        var browserField = typeof(DocumentPdfService)
            .GetField("_browser", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

        Assert.Null(browserField);

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
}
