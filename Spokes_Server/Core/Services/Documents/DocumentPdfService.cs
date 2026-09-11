using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Services.Documents;

/// <summary>
/// Service that generates PDF documents from Blazor URLs using PuppeteerSharp.
/// NOTE: PuppeteerSharp (a headless Chromium browser) is used intentionally to allow full rendering of Blazor pages,
/// CSS print styles, and MudBlazor components. Light/native PDF libraries (like QuestPDF or SkiaSharp) cannot parse HTML/CSS/JS,
/// and would require completely rebuilding the document rendering engine from scratch.
/// </summary>
public class DocumentPdfService : IDisposable
{
    private readonly RenderTokenService _tokenService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<DocumentPdfService> _logger;
    private readonly string _dataPath;
    private IBrowser? _browser;

    public DocumentPdfService(RenderTokenService tokenService, IHttpContextAccessor httpContextAccessor, Microsoft.Extensions.Configuration.IConfiguration config, ILogger<DocumentPdfService> logger)
    {
        _tokenService = tokenService;
        _httpContextAccessor = httpContextAccessor;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public void Dispose()
    {
        if (_browser != null)
        {
            _browser.Dispose();
            _browser = null;
        }
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null && !_browser.IsClosed) return _browser;

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
        });

        return _browser;
    }

    public async Task<string> GenerateQuotePdfAsync(Quote quote, string projectId, string baseUrl)
    {
        var relativeUrl = $"/projects/{projectId}/quotes/{quote.Id}";
        var pdfPath = GetPdfPath("Quotes", quote.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath);
        return pdfPath;
    }

    public async Task<string> GenerateInvoicePdfAsync(Invoice invoice, string projectId, string baseUrl)
    {
        var relativeUrl = $"/projects/{projectId}/invoices/{invoice.Id}";
        var pdfPath = GetPdfPath("Invoices", invoice.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath);
        return pdfPath;
    }

    public async Task<string> GenerateProjectDocumentPdfAsync(ProjectDocument doc, string projectId, string baseUrl)
    {
        var relativeUrl = $"/projects/{projectId}/documents/{doc.Id}";
        var pdfPath = GetPdfPath("ProjectDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath);
        return pdfPath;
    }

    public async Task<string> GenerateBusinessDocumentPdfAsync(ProjectDocument doc, string baseUrl)
    {
        var relativeUrl = $"/business/documents/{doc.Id}";
        var pdfPath = GetPdfPath("BusinessDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath);
        return pdfPath;
    }

    public async Task<string> GeneratePurchaseOrderPdfAsync(PurchaseOrder po, string baseUrl)
    {
        var relativeUrl = $"/purchases/{po.Id}";
        var pdfPath = GetPdfPath("PurchaseOrders", po.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath);
        return pdfPath;
    }

    private async Task GeneratePdfAsync(string baseUrl, string relativeUrl, string destinationPath)
    {
        try
        {
            _logger.LogInformation("Generating PDF: {DestinationPath}", destinationPath);
            var token = _tokenService.CreateToken();
            var url = $"{baseUrl.TrimEnd('/')}{relativeUrl}?renderToken={token}";

            _logger.LogInformation("PDF URL configured as: {Url}", url);

            _logger.LogInformation("Getting browser payload...");
            var browser = await GetBrowserAsync();

            _logger.LogInformation("Opening new browser page...");
            await using var page = await browser.NewPageAsync();

            _logger.LogInformation("Emulating Print Media Type...");
            // 1. Emulate print media type so @media print CSS applies
            await page.EmulateMediaTypeAsync(MediaType.Print);

            _logger.LogInformation("Navigating to URL...");
            // 2. Navigate and wait until network is mostly idle (so fonts/images load)
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }
            });

            _logger.LogInformation("Waiting for layout shift completion...");
            // 3. Optional: Give it an extra second in case there are subtle layout shifts or delayed Blazor rendering
            await Task.Delay(1000);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(destinationPath);
            if (dir != null && !Directory.Exists(dir))
            {
                _logger.LogInformation("Creating directory: {Dir}", dir);
                Directory.CreateDirectory(dir);
            }

            _logger.LogInformation("Printing to PDF...");
            await page.PdfAsync(destinationPath, new PdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true
            });
            _logger.LogInformation("PDF successfully generated at {DestinationPath}", destinationPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate PDF for URL {RelativeUrl}", relativeUrl);
            throw; // Rethrow to let the caller handle it if they choose to await it in the future
        }
    }



    private string GetPdfPath(string folderName, string documentId)
    {
        // Example: Data/PDFs/Quotes/quote-123.pdf
        return Path.GetFullPath(Path.Combine(_dataPath, "PDFs", folderName, $"{documentId}.pdf"));
    }
}
