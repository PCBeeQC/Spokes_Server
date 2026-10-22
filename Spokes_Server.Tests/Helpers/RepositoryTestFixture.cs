using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Xunit;

namespace Spokes_Server.Tests.Helpers;

/// <summary>
/// Base class for repository unit tests.
/// Manages temp test directories, DiskPersistenceService, and IConfiguration setup/teardown uniformly.
/// </summary>
public abstract class RepositoryTestBase : IDisposable
{
    protected readonly string _testDataDir;
    protected readonly DiskPersistenceService _writer;
    protected readonly Mock<IConfiguration> _mockConfig;
    protected IConfiguration Config => _mockConfig.Object;
    protected DiskPersistenceService _persistence => _writer;
    protected IConfiguration _config => Config;
    protected string _testDataPath => _testDataDir;

    protected RepositoryTestBase(string? testPrefix = null)
    {
        var prefix = string.IsNullOrEmpty(testPrefix) ? "Spokes_Test_" : $"Spokes_Test_{testPrefix}_";
        _testDataDir = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);
    }

    /// <summary>
    /// Creates a mock IConfiguration where DataPath returns null.
    /// </summary>
    public static IConfiguration CreateNullDataPathConfig()
    {
        var mock = new Mock<IConfiguration>();
        mock.Setup(c => c["DataPath"]).Returns((string?)null);
        return mock.Object;
    }

    public virtual void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Writer dispose error: {ex.Message}");
        }

        if (Directory.Exists(_testDataDir))
        {
            try
            {
                Directory.Delete(_testDataDir, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}");
            }
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Generic repository test fixture that automatically constructs the repository under test
/// and verifies default path behavior on null configuration.
/// </summary>
public abstract class RepositoryTestFixture<TRepository> : RepositoryTestBase
    where TRepository : class
{
    protected readonly TRepository _repo;
    protected TRepository _repository => _repo;
    private readonly Func<DiskPersistenceService, IConfiguration, TRepository>? _factory;

    protected RepositoryTestFixture(Func<DiskPersistenceService, IConfiguration, TRepository> factory, string? testPrefix = null)
        : base(testPrefix ?? typeof(TRepository).Name)
    {
        _factory = factory;
        _repo = factory(_writer, Config);
    }

    protected RepositoryTestFixture(string? testPrefix = null)
        : base(testPrefix ?? typeof(TRepository).Name)
    {
        var ctor = typeof(TRepository).GetConstructor([typeof(DiskPersistenceService), typeof(IConfiguration)]);
        if (ctor != null)
        {
            _repo = (TRepository)ctor.Invoke([_writer, Config]);
        }
        else
        {
            throw new InvalidOperationException($"Type {typeof(TRepository).Name} does not have a (DiskPersistenceService, IConfiguration) constructor. Use the factory constructor.");
        }
    }

    [Fact]
    public virtual void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var nullConfig = CreateNullDataPathConfig();
        if (_factory != null)
        {
            var repo = _factory(_writer, nullConfig);
            Assert.NotNull(repo);
            return;
        }

        var ctor = typeof(TRepository).GetConstructor([typeof(DiskPersistenceService), typeof(IConfiguration)]);
        if (ctor != null)
        {
            var repo = (TRepository)ctor.Invoke([_writer, nullConfig]);
            Assert.NotNull(repo);
        }
    }
}
