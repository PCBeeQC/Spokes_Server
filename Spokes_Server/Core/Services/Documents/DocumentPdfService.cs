using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Services.Documents;

/// <summary>
/// Service that generates PDF documents from Blazor URLs using PuppeteerSharp.
/// NOTE: PuppeteerSharp (a headless Chromium browser) is used intentionally to allow full rendering of Blazor pages,
/// CSS print styles, and MudBlazor components. Light/native PDF libraries (like QuestPDF or SkiaSharp) cannot parse HTML/CSS/JS,
/// and would require completely rebuilding the document rendering engine from scratch.
/// </summary>
public class DocumentPdfService
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

    public async Task<string> GenerateQuotePdfAsync(Quote quote, string projectId, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/quotes/{quote.Id}";
        var pdfPath = GetPdfPath("Quotes", quote.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateInvoicePdfAsync(Invoice invoice, string projectId, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/invoices/{invoice.Id}";
        var pdfPath = GetPdfPath("Invoices", invoice.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateProjectDocumentPdfAsync(ProjectDocument doc, string projectId, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/documents/{doc.Id}";
        var pdfPath = GetPdfPath("ProjectDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateProjectReportPdfAsync(ProjectReport report, string projectId, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/projects/{projectId}/reports/{report.Id}";
        var pdfPath = GetPdfPath("ProjectReports", report.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public virtual async Task<byte[]> GenerateEphemeralProjectReportPdfBytesAsync(
        ProjectReport report,
        string initiatorEmployeeId,
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var relativeUrl = $"/projects/{report.ProjectId}/reports/{report.Id}?print=true";
        var effectiveBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? (_httpContextAccessor.HttpContext != null
                ? $"{_httpContextAccessor.HttpContext.Request.Scheme}://{_httpContextAccessor.HttpContext.Request.Host}"
                : "http://127.0.0.1:5000")
            : baseUrl;
        var tempPath = Path.Combine(Path.GetTempPath(), $"project-report-{report.Id}-{Guid.NewGuid():N}.pdf");
        try
        {
            await GeneratePdfAsync(effectiveBaseUrl, relativeUrl, tempPath, initiatorEmployeeId, cancellationToken);
            return await File.ReadAllBytesAsync(tempPath, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<string> GenerateBusinessDocumentPdfAsync(ProjectDocument doc, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/business/documents/{doc.Id}";
        var pdfPath = GetPdfPath("BusinessDocuments", doc.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GeneratePurchaseOrderPdfAsync(PurchaseOrder po, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/purchases/{po.Id}";
        var pdfPath = GetPdfPath("PurchaseOrders", po.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
        return pdfPath;
    }

    public async Task<string> GenerateFinancialReportPdfAsync(FinancialReport report, string baseUrl, string? initiatorEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"/business/accounting/financial-reports/{report.Id}";
        var pdfPath = GetPdfPath("FinancialReports", report.Id);
        await GeneratePdfAsync(baseUrl, relativeUrl, pdfPath, initiatorEmployeeId, cancellationToken);
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
            var expectedPath = expectedRelativeUrl.Split('?', 2)[0].TrimEnd('/');

            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PDF generation aborted: headless browser was redirected from '{expectedRelativeUrl}' to '{currentUri.AbsolutePath}'.");
            }
        }
    }

    private static readonly HashSet<string> AllowedDataMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/gif",
        "image/webp",
        "image/bmp",
        "image/x-icon",
        "image/vnd.microsoft.icon",
        "font/woff",
        "font/woff2",
        "font/ttf",
        "font/otf",
        "application/font-woff",
        "application/font-woff2",
        "application/x-font-woff",
        "application/x-font-ttf",
        "application/vnd.ms-fontobject"
    };

    /// <summary>
    /// Enforces a strict same-origin SSRF boundary on all headless Chromium page and subresource requests.
    /// Permits only <c>about:blank</c>, non-executable inline image/font <c>data:</c> URIs, and requests whose
    /// scheme/host/port exactly match <paramref name="effectiveBaseUrl"/> (plus matching <c>ws:</c>/<c>wss:</c> transport).
    /// </summary>
    internal static bool IsAllowedPdfRenderRequestUrl(string? requestUrl, string effectiveBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            return false;
        }

        if (string.Equals(requestUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (requestUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = requestUrl.AsSpan(5);
            var delimiterIdx = payload.IndexOfAny(';', ',');
            if (delimiterIdx <= 0)
            {
                return false;
            }

            var mediaType = payload[..delimiterIdx].Trim().ToString();
            return AllowedDataMediaTypes.Contains(mediaType);
        }

        if (!Uri.TryCreate(effectiveBaseUrl, UriKind.Absolute, out var baseUri) ||
            !Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(reqUri.UserInfo))
        {
            return false;
        }

        var schemeMatches = string.Equals(reqUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reqUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reqUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase));

        if (!schemeMatches)
        {
            return false;
        }

        return string.Equals(reqUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && reqUri.Port == baseUri.Port;
    }

    private async Task GeneratePdfAsync(
        string baseUrl,
        string relativeUrl,
        string destinationPath,
        string? initiatorEmployeeId = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedInitiatorId = string.IsNullOrWhiteSpace(initiatorEmployeeId)
            ? _httpContextAccessor.HttpContext?.User.FindFirst("EmployeeId")?.Value
            : initiatorEmployeeId.Trim();
        if (string.IsNullOrWhiteSpace(resolvedInitiatorId) ||
            string.Equals(resolvedInitiatorId, RenderTokenMiddleware.SystemRendererId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Headless PDF generation requires an authenticated non-system initiator employee.");
        }

        string? token = null;
        IBrowserContext? browserContext = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Generating PDF: {DestinationPath}", destinationPath);
            token = _tokenService.CreateToken(relativeUrl, resolvedInitiatorId);
            var effectiveBaseUrl = ResolveEffectiveBaseUrl(baseUrl);
            var querySeparator = relativeUrl.Contains('?') ? '&' : '?';
            var url = $"{effectiveBaseUrl}{relativeUrl}{querySeparator}renderToken={token}";

            _logger.LogInformation("PDF URL configured as: {Url}", url);

            _logger.LogInformation("Getting browser payload...");
            var browser = await _browserManager.GetBrowserAsync(cancellationToken);

            _logger.LogInformation("Creating isolated browser context and opening new page...");
            browserContext = await browser.CreateBrowserContextAsync();
            await using var page = await browserContext.NewPageAsync();
            await page.SetRequestInterceptionAsync(true);

            EventHandler<RequestEventArgs> requestHandler = async (_, e) =>
            {
                try
                {
                    if (IsAllowedPdfRenderRequestUrl(e.Request?.Url, effectiveBaseUrl))
                    {
                        await e.Request!.ContinueAsync();
                    }
                    else
                    {
                        _logger.LogWarning("Blocked off-origin PDF render subrequest: {RequestUrl}", e.Request?.Url);
                        if (e.Request != null)
                        {
                            await e.Request.AbortAsync();
                        }
                    }
                }
                catch
                {
                    try
                    {
                        if (e.Request != null)
                        {
                            await e.Request.AbortAsync();
                        }
                    }
                    catch
                    {
                    }
                }
            };

            page.Request += requestHandler;
            try
            {
                _logger.LogInformation("Configuring desktop viewport for document pagination...");
                // 1. Configure a desktop viewport so #printable-area (max-width: 8.5in / 816px) lays out at full width
                await page.SetViewportAsync(new ViewPortOptions
                {
                    Width = 1280,
                    Height = 1056
                });

                _logger.LogInformation("Navigating to URL...");
                // 2. Navigate in screen media mode (matching the interactive viewer) and wait until network is mostly idle
                await page.GoToAsync(url, new NavigationOptions
                {
                    WaitUntil = [WaitUntilNavigation.Networkidle0]
                });

                // 3. Wait for Blazor Interactive Server circuit to render and paginate the printable container
                try
                {
                    await page.WaitForSelectorAsync("[data-paginated-for=\"printable-area\"], #printable-area[data-pagination-complete=\"true\"]", new WaitForSelectorOptions
                    {
                        Timeout = 10000
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Timed out waiting for paginated container on {RelativeUrl} (current page URL: {CurrentUrl})", relativeUrl, page.Url);
                }

                _logger.LogInformation("Emulating Print Media Type...");
                // 4. Emulate print media type after pagination completes, identical to browser window.print()
                await page.EmulateMediaTypeAsync(MediaType.Print);

                _logger.LogInformation("Waiting for layout shift completion...");
                // 5. Give a brief settle delay before printing
                await Task.Delay(250, cancellationToken);

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
            finally
            {
                page.Request -= requestHandler;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate PDF for URL {RelativeUrl}", relativeUrl);
            throw; // Rethrow to let the caller handle it if they choose to await it in the future
        }
        finally
        {
            if (browserContext != null)
            {
                try
                {
                    await browserContext.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to close isolated headless browser context");
                }
            }

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
