using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Spokes_Server.Core.Services.Documents;

/// <summary>
/// Singleton manager responsible for Chromium browser lifecycle, synchronization, and graceful shutdown.
/// Protects against concurrent launch races, process leakage, and unmonitored provisioning failures.
/// Supports configurable local Chromium executable fallback (e.g. BrowserManager:ExecutablePath or PUPPETEER_EXECUTABLE_PATH)
/// to avoid runtime binary downloads in containerized or air-gapped environments.
/// </summary>
public class BrowserManager : IBrowserManager
{
    private readonly ILogger<BrowserManager> _logger;
    private readonly IConfiguration? _configuration;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IBrowser? _browser;
    private volatile bool _isProvisioned;
    private bool _isDisposed;

    public bool IsReady => _isProvisioned && _browser != null && !_browser.IsClosed;

    public string? ExecutablePath => ResolveExecutablePath();

    public BrowserManager(ILogger<BrowserManager> logger, IConfiguration? configuration = null)
    {
        _logger = logger;
        _configuration = configuration;
    }

    private string? GetRawConfiguredPath()
    {
        return _configuration?["BrowserManager:ExecutablePath"]
            ?? _configuration?["PUPPETEER_EXECUTABLE_PATH"]
            ?? _configuration?["CHROME_PATH"]
            ?? _configuration?["CHROMIUM_PATH"]
            ?? Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")
            ?? Environment.GetEnvironmentVariable("CHROME_PATH")
            ?? Environment.GetEnvironmentVariable("CHROMIUM_PATH");
    }

    private string? ResolveExecutablePath()
    {
        var rawPath = GetRawConfiguredPath();
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            var trimmed = rawPath.Trim();
            if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    public async Task EnsureProvisionedAsync(CancellationToken cancellationToken = default)
    {
        if (_isProvisioned) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_isProvisioned) return;

            var localExecutable = ResolveExecutablePath();
            if (!string.IsNullOrWhiteSpace(localExecutable))
            {
                _logger.LogInformation("[BrowserManager] Using pre-installed Chromium executable at {Path}; skipping runtime download.", localExecutable);
                _isProvisioned = true;
                return;
            }

            var rawPath = GetRawConfiguredPath();
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                _logger.LogWarning("[BrowserManager] Configured Chromium executable path '{Path}' not found on disk. Falling back to BrowserFetcher.", rawPath);
            }

            _logger.LogInformation("[BrowserManager] Provisioning Chromium browser binaries via BrowserFetcher...");
            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync().WaitAsync(cancellationToken);
            _isProvisioned = true;
            _logger.LogInformation("[BrowserManager] Chromium binaries successfully provisioned.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserManager] Failed to provision Chromium binaries.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_browser != null && !_browser.IsClosed)
        {
            return _browser;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_browser != null && !_browser.IsClosed)
            {
                return _browser;
            }

            var localExecutable = ResolveExecutablePath();
            if (!string.IsNullOrWhiteSpace(localExecutable))
            {
                _isProvisioned = true;
            }
            else if (!_isProvisioned)
            {
                _logger.LogInformation("[BrowserManager] Chromium not yet provisioned; ensuring download before launch...");
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync().WaitAsync(cancellationToken);
                _isProvisioned = true;
            }

            _logger.LogInformation("[BrowserManager] Launching headless Chromium browser instance...");
            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            };

            if (!string.IsNullOrWhiteSpace(localExecutable))
            {
                _logger.LogInformation("[BrowserManager] Launching with local Chromium binary at {Path}", localExecutable);
                launchOptions.ExecutablePath = localExecutable;
            }

            _browser = await Puppeteer.LaunchAsync(launchOptions);

            return _browser;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _lock.WaitAsync();
        try
        {
            if (_browser != null)
            {
                _logger.LogInformation("[BrowserManager] Gracefully closing Chromium browser...");
                try
                {
                    if (!_browser.IsClosed)
                    {
                        await _browser.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BrowserManager] Exception during browser close.");
                }
                finally
                {
                    _browser.Dispose();
                    _browser = null;
                }
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
