using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using PuppeteerSharp;
using Spokes_Server.Core.Services.Documents;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Documents;

public class BrowserManagerTests
{
    private readonly Mock<ILogger<BrowserManager>> _mockLogger;

    public BrowserManagerTests()
    {
        _mockLogger = new Mock<ILogger<BrowserManager>>();
    }

    [Fact]
    public void IsReady_Initially_ReturnsFalse()
    {
        using var manager = new BrowserManager(_mockLogger.Object);
        Assert.False(manager.IsReady);
    }

    [Fact]
    public async Task DisposeAsync_WhenNoBrowserLaunched_DoesNotThrow()
    {
        var manager = new BrowserManager(_mockLogger.Object);
        await manager.DisposeAsync();
        Assert.False(manager.IsReady);
    }

    [Fact]
    public async Task GetBrowserAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        var manager = new BrowserManager(_mockLogger.Object);
        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetBrowserAsync());
    }

    [Fact]
    public async Task Dispose_CallsDisposeAsyncCleanly()
    {
        var manager = new BrowserManager(_mockLogger.Object);
        manager.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetBrowserAsync());
    }

    [Fact]
    public void ExecutablePath_WhenNotConfigured_ReturnsNull()
    {
        using var manager = new BrowserManager(_mockLogger.Object);
        Assert.Null(manager.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_WhenConfiguredPathDoesNotExist_ReturnsNull()
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["BrowserManager:ExecutablePath"] = @"C:\NonExistent\chrome.exe"
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        using var manager = new BrowserManager(_mockLogger.Object, config);
        Assert.Null(manager.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_WhenConfiguredPathExists_ReturnsConfiguredPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var inMemory = new Dictionary<string, string?>
            {
                ["BrowserManager:ExecutablePath"] = tempFile
            };
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(inMemory)
                .Build();

            using var manager = new BrowserManager(_mockLogger.Object, config);
            Assert.Equal(tempFile, manager.ExecutablePath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EnsureProvisionedAsync_WhenCustomExecutableExists_ProvisionsWithoutDownload()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var inMemory = new Dictionary<string, string?>
            {
                ["BrowserManager:ExecutablePath"] = tempFile
            };
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(inMemory)
                .Build();

            using var manager = new BrowserManager(_mockLogger.Object, config);
            await manager.EnsureProvisionedAsync();

            Assert.Equal(tempFile, manager.ExecutablePath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
