using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class UserClientStateRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly UserClientStateRepository _repo;

    public UserClientStateRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ClientStates_{Guid.NewGuid()}");

        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _repo = new UserClientStateRepository(_writer, _mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
        _writer.Dispose();
    }

    [Fact]
    public void SaveDraft_And_GetDraft_ReturnsSavedDraft()
    {
        // Act
        _repo.SaveDraft("user1", "chan1", "Hello draft");

        // Assert
        var draft = _repo.GetDraft("user1", "chan1");
        Assert.Equal("Hello draft", draft);

        var state = _repo.GetByUserId("user1");
        Assert.NotNull(state);
        Assert.Equal("chan1", state!.ActiveChannelId);
        Assert.True(state.ChannelDrafts.ContainsKey("chan1"));
    }

    [Fact]
    public void ClearDraft_RemovesDraft_AndDeletesStateIfEmpty()
    {
        // Arrange
        _repo.SaveDraft("user1", "chan1", "Hello draft");
        Assert.Equal("Hello draft", _repo.GetDraft("user1", "chan1"));

        // Act
        _repo.ClearDraft("user1", "chan1");

        // Assert
        Assert.Null(_repo.GetDraft("user1", "chan1"));
        Assert.Null(_repo.GetByUserId("user1"));
    }

    [Fact]
    public void ClearDraft_MultipleDrafts_KeepsOtherDrafts()
    {
        // Arrange
        _repo.SaveDraft("user1", "chan1", "Draft 1");
        _repo.SaveDraft("user1", "chan2", "Draft 2");

        // Act
        _repo.ClearDraft("user1", "chan1");

        // Assert
        Assert.Null(_repo.GetDraft("user1", "chan1"));
        Assert.Equal("Draft 2", _repo.GetDraft("user1", "chan2"));
        Assert.NotNull(_repo.GetByUserId("user1"));
    }
}
