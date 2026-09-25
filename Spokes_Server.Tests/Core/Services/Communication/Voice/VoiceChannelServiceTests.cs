using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceChannelServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly Mock<Database> _mockDb;
    private readonly VoiceChannelService _service;

    public VoiceChannelServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_VoiceChannelService_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
        
        // Set dummy LiveKit credentials
        var sysConfig = _systemConfigs.Get();
        sysConfig.LiveKitApiKey = "test-api-key";
        sysConfig.LiveKitApiSecret = "test-api-secret-which-must-be-long-enough";
        _systemConfigs.Save(sysConfig);

        _mockDb = new Mock<Database>((DeviceSessionRepository)null!, (EmployeeRepository)null!);
        _mockDb.Setup(d => d.SystemConfigs).Returns(_systemConfigs);

        _service = new VoiceChannelService(_mockDb.Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void GenerateJoinToken_ValidInputs_ReturnsJwtString()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";
        string userAvatarUrl = "http://example.com/avatar.png";

        // Act
        var result = _service.GenerateJoinToken(channelId, userId, userName, userAvatarUrl);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("eyJ", result); // JWTs typically start with eyJ
    }

    [Fact]
    public void GenerateJoinToken_EmptyAvatar_ReturnsJwtString()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";

        // Act
        var result = _service.GenerateJoinToken(channelId, userId, userName, "");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("eyJ", result);
    }

    [Fact]
    public void GenerateJoinToken_NullAvatar_ThrowsNullReferenceException()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";

        // Act & Assert
        Assert.Throws<NullReferenceException>(() => 
            _service.GenerateJoinToken(channelId, userId, userName, null));
    }
}
