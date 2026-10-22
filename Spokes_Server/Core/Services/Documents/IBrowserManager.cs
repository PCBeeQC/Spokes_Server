using System;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace Spokes_Server.Core.Services.Documents;

/// <summary>
/// Contract for managing headless Chromium browser lifecycle, provisioning, and synchronization.
/// </summary>
public interface IBrowserManager : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a running Chromium browser instance, launching one if necessary under thread-safe synchronization.
    /// </summary>
    Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that Chromium binaries are downloaded and ready for use.
    /// </summary>
    Task EnsureProvisionedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether Chromium is provisioned and ready for document generation.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the resolved local executable path for Chromium, if explicitly configured or discovered.
    /// </summary>
    string? ExecutablePath { get; }
}
