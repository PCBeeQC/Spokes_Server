using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Spokes_Server.Core.Data.Repositories.Core;
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
    private static readonly byte[] AsciiAccessDenied = Encoding.ASCII.GetBytes("/access-denied");
    private static readonly byte[] Utf16BeAccessDenied = Encoding.BigEndianUnicode.GetBytes("/access-denied");
    private static readonly byte[] AsciiSsoLogin = Encoding.ASCII.GetBytes("/sso/login");
    private static readonly byte[] Utf16BeSsoLogin = Encoding.BigEndianUnicode.GetBytes("/sso/login");

    private readonly RenderTokenService _tokenService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<DocumentPdfService> _logger;
    private readonly string _dataPath;
    private readonly IBrowserManager _browserManager;
    private readonly IServer? _server;
    private readonly DeviceSessionRepository? _deviceSessions;
    private IBrowser? _browser;

    public DocumentPdfService(
        RenderTokenService tokenService,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration config,
        ILogger<DocumentPdfService> logger,
        IBrowserManager browserManager,
        IServer? server = null,
        DeviceSessionRepository? deviceSessions = null)
    {
        _tokenService = tokenService;
        _httpContextAccessor = httpContextAccessor;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
        _browserManager = browserManager ?? throw new ArgumentNullException(nameof(browserManager));
        _server = server;
        _deviceSessions = deviceSessions;
    }

    internal string DataPath => _dataPath;

    internal IBrowser? Browser
    {
        get => _browser;
        set => _browser = value;
    }

    public void Dispose()
    {
        if (_browser != null)
        {
            _browser.Dispose();
            _browser = null;
        }
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (_browser != null && !_browser.IsClosed) return _browser;

        return await _browserManager.GetBrowserAsync(cancellationToken);
    }

    public async Task<string> GenerateQuotePdfAsync(Quote quote, string projectId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/quotes/{quote.Id}";
        var pdfPath = GetPdfPath("Quotes", quote.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateInvoicePdfAsync(Invoice invoice, string projectId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/invoices/{invoice.Id}";
        var pdfPath = GetPdfPath("Invoices", invoice.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateProjectDocumentPdfAsync(ProjectDocument doc, string projectId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/documents/{doc.Id}";
        var pdfPath = GetPdfPath("ProjectDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateBusinessDocumentPdfAsync(ProjectDocument doc, string baseUrl, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/business/documents/{doc.Id}";
        var pdfPath = GetPdfPath("BusinessDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GeneratePurchaseOrderPdfAsync(PurchaseOrder po, string baseUrl, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/purchases/{po.Id}";
        var pdfPath = GetPdfPath("PurchaseOrders", po.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, cancellationToken);
        return pdfPath;
    }

    /// <summary>
    /// Verifies that an existing cached PDF file exists on disk, is non-empty, and does not
    /// contain a captured authentication redirect page (such as /access-denied or /sso/login).
    /// If a corrupted redirect PDF is detected, it is automatically deleted so the caller can regenerate it.
    /// </summary>
    public bool IsValidCachedPdf(string? pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            if (bytes.Length == 0)
            {
                TryDeleteCorruptedPdf(pdfPath, "empty file");
                return false;
            }

            ReadOnlySpan<byte> span = bytes;
            if (span.IndexOf(AsciiAccessDenied) >= 0 ||
                span.IndexOf(Utf16BeAccessDenied) >= 0 ||
                span.IndexOf(AsciiSsoLogin) >= 0 ||
                span.IndexOf(Utf16BeSsoLogin) >= 0)
            {
                TryDeleteCorruptedPdf(pdfPath, "captured redirect/error page");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate cached PDF at {PdfPath}", pdfPath);
            return true;
        }
    }

    private void TryDeleteCorruptedPdf(string pdfPath, string reason)
    {
        _logger.LogWarning("Deleting invalid cached PDF at {PdfPath} ({Reason}) so it can be regenerated.", pdfPath, reason);
        try
        {
            File.Delete(pdfPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete invalid cached PDF at {PdfPath}", pdfPath);
        }
    }

    internal string ResolveEffectiveBaseUrl(string fallbackBaseUrl)
    {
        var addresses = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var loopback = TryResolveLoopbackAddress(addresses);
        if (!string.IsNullOrEmpty(loopback))
        {
            return loopback;
        }

        return fallbackBaseUrl.TrimEnd('/');
    }

    internal static string? TryResolveLoopbackAddress(IEnumerable<string>? addresses)
    {
        if (addresses == null) return null;

        string? httpsFallback = null;
        foreach (var raw in addresses)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var normalized = raw.Trim()
                .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://[::]:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://localhost:", "://127.0.0.1:", StringComparison.OrdinalIgnoreCase);

            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                var candidate = $"{uri.Scheme}://127.0.0.1:{uri.Port}";
                if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                httpsFallback ??= candidate;
            }
        }

        return httpsFallback;
    }

    internal static void ValidateRenderedPage(string? currentUrl, string expectedRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            return;
        }

        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
        {
            var actualPath = currentUri.AbsolutePath.TrimEnd('/');
            var expectedPath = expectedRelativeUrl.TrimEnd('/');

            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PDF generation aborted: headless browser was redirected from '{expectedRelativeUrl}' to '{currentUri.AbsolutePath}'.");
            }
        }
    }

    private async Task GeneratePdfAsync(string baseUrl, string relativeUrl, string destinationPath, CancellationToken cancellationToken = default)
    {
        string? token = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Generating PDF: {DestinationPath}", destinationPath);
            token = _tokenService.CreateToken();
            var effectiveBaseUrl = ResolveEffectiveBaseUrl(baseUrl);
            var url = $"{effectiveBaseUrl}{relativeUrl}?renderToken={token}";

            _logger.LogInformation("PDF URL configured as: {Url}", url);

            _logger.LogInformation("Getting browser payload...");
            var browser = await GetBrowserAsync(cancellationToken);

            _logger.LogInformation("Opening new browser page...");
            await using var page = await browser.NewPageAsync();

            _logger.LogInformation("Setting extra HTTP headers...");
            await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
            {
                ["X-Spokes-Render-Token"] = token
            });

            _logger.LogInformation("Emulating Print Media Type...");
            // 1. Emulate print media type so @media print CSS applies
            await page.EmulateMediaTypeAsync(MediaType.Print);

            _logger.LogInformation("Navigating to URL...");
            // 2. Navigate and wait until network is mostly idle (so fonts/images load)
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0]
            });

            // 3. Wait for Blazor Interactive Server circuit to render the printable container
            try
            {
                await page.WaitForSelectorAsync("#printable-area", new WaitForSelectorOptions
                {
                    Timeout = 10000
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timed out waiting for #printable-area on {RelativeUrl} (current page URL: {CurrentUrl})", relativeUrl, page.Url);
            }

            _logger.LogInformation("Waiting for layout shift completion...");
            // 4. Give it an extra second in case there are subtle layout shifts, JS pagination, or delayed Blazor rendering
            await Task.Delay(1000, cancellationToken);

            // 5. Ensure the headless browser was not redirected to /access-denied, /sso/login, or /projects
            ValidateRenderedPage(page.Url, relativeUrl);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(destinationPath);
            if (dir != null && !Directory.Exists(dir))
            {
                _logger.LogInformation("Creating directory: {Dir}", dir);
                Directory.CreateDirectory(dir);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        finally
        {
            if (!string.IsNullOrEmpty(token) && _tokenService.RevokeToken(token, out var sessionId) && !string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    _deviceSessions?.Delete(sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temporary headless render session {SessionId}", sessionId);
                }
            }
        }
    }

    private string GetPdfPath(string folderName, string documentId)
    {
        // Example: Data/PDFs/Quotes/quote-123.pdf
        return Path.GetFullPath(Path.Combine(_dataPath, "PDFs", folderName, $"{documentId}.pdf"));
    }
}
