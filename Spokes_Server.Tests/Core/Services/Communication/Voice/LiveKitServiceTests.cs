using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class LiveKitServiceTests : IDisposable
{
    private readonly Mock<ILogger<LiveKitService>> _loggerMock;
    private readonly LiveKitService _service;

    public LiveKitServiceTests()
    {
        _loggerMock = new Mock<ILogger<LiveKitService>>();
        _service = new LiveKitService(_loggerMock.Object);
    }

    public void Dispose()
    {
    }

    [Fact]
    public void ApplyPortConfiguration_NullConfig_LogsError()
    {
        // Arrange
        SystemConfig? config = null;

        // Act
        _service.ApplyPortConfiguration(config);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public void ApplyPortConfiguration_ValidConfig_LogsErrorOnWindowsDueToMissingPaths()
    {
        // Arrange
        var config = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 50000,
            LiveKitUdpEndPort = 50100,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        // Act
        _service.ApplyPortConfiguration(config);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public async System.Threading.Tasks.Task RemoveParticipantAsync_WhenDaemonUnreachable_LogsWarningGracefully()
    {
        // Act
        await _service.RemoveParticipantAsync("chan-test", "user-test", "dummy-key", "dummy-secret");

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public void ApplyPortConfiguration_WithConfigurationProvided_DoesNotThrow()
    {
        var configDict = new Dictionary<string, string?>
        {
            ["Spokes_Voice_Port"] = "7880",
            ["Spokes_Voice_Fallback_Port"] = "7881",
            ["Spokes_Voice_UDP_Start_Port"] = "50000",
            ["Spokes_VOICE_UDP_PORT_COUNT"] = "100",
            ["Spokes_LiveKit_Log_Level"] = "debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var service = new LiveKitService(_loggerMock.Object, configuration);

        var systemConfig = new SystemConfig
        {
            IsSetupComplete = false,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        var ex = Record.Exception(() => service.ApplyPortConfiguration(systemConfig));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null, "info")]
    [InlineData("", "info")]
    [InlineData("   ", "info")]
    [InlineData("INFO", "info")]
    [InlineData("debug", "debug")]
    [InlineData("DEBUG", "debug")]
    [InlineData("warn", "warn")]
    [InlineData("WARNING", "warn")]
    [InlineData("error", "error")]
    [InlineData("ERROR", "error")]
    [InlineData("unknown", "info")]
    public void NormalizeLogLevel_ReturnsExpectedNormalizedValue(string? input, string expected)
    {
        Assert.Equal(expected, LiveKitService.NormalizeLogLevel(input));
    }

    [Fact]
    public void BuildConfigurationYaml_DefaultInfoLevel_EmitsUdpMuxAndSampledInfoWithApiWarn()
    {
        var systemConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 30000,
            LiveKitUdpEndPort = 30007,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret",
            LiveKitLogLevel = "info"
        };

        var yaml = _service.BuildConfigurationYaml(systemConfig);

        Assert.Contains("udp_port: 30000-30007", yaml);
        Assert.DoesNotContain("port_range_start", yaml);
        Assert.DoesNotContain("port_range_end", yaml);
        Assert.Contains("level: info", yaml);
        Assert.Contains("pion_level: error", yaml);
        Assert.Contains("sample: true", yaml);
        Assert.Contains("component_levels:", yaml);
        Assert.Contains("api: warn", yaml);
        Assert.DoesNotContain("room: debug", yaml);
    }

    [Fact]
    public void BuildConfigurationYaml_SinglePortUdpMux_EmitsSinglePortWithoutHyphen()
    {
        var systemConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 30000,
            LiveKitUdpEndPort = 30000,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        var yaml = _service.BuildConfigurationYaml(systemConfig);

        Assert.Contains("udp_port: 30000\n", yaml.Replace("\r\n", "\n"));
        Assert.DoesNotContain("udp_port: 30000-30000", yaml);
        Assert.DoesNotContain("port_range_start", yaml);
    }

    [Fact]
    public void BuildConfigurationYaml_CustomValidMuxRange_EmitsExactRange()
    {
        var systemConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 40000,
            LiveKitUdpEndPort = 40015,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        var yaml = _service.BuildConfigurationYaml(systemConfig);

        Assert.Contains("udp_port: 40000-40015", yaml);
    }

    [Fact]
    public void BuildConfigurationYaml_OversizedLegacyRangeOrEnvCount_ClampsToEightPortsPreservingCustomStart()
    {
        var legacyRestoredConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitUdpStartPort = 50000,
            LiveKitUdpEndPort = 50499,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        var restoredYaml = _service.BuildConfigurationYaml(legacyRestoredConfig);
        Assert.Contains("udp_port: 50000-50007", restoredYaml);

        var envConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spokes_Voice_UDP_Start_Port"] = "45000",
            ["Spokes_VOICE_UDP_PORT_COUNT"] = "500"
        }).Build();
        var envService = new LiveKitService(_loggerMock.Object, envConfig);
        var preSetupConfig = new SystemConfig
        {
            IsSetupComplete = false,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret"
        };

        var envYaml = envService.BuildConfigurationYaml(preSetupConfig);
        Assert.Contains("udp_port: 45000-45007", envYaml);
    }

    [Theory]
    [InlineData(30000, 30007, UdpPortRangeValidationError.None)]
    [InlineData(30000, 30000, UdpPortRangeValidationError.None)]
    [InlineData(40000, 40063, UdpPortRangeValidationError.None)]
    [InlineData(80, 87, UdpPortRangeValidationError.InvalidStartPort)]
    [InlineData(30000, 70000, UdpPortRangeValidationError.InvalidEndPort)]
    [InlineData(30010, 30005, UdpPortRangeValidationError.EndBeforeStart)]
    [InlineData(30000, 30064, UdpPortRangeValidationError.ExceedsMaxMuxPorts)]
    public void ValidateUdpMuxRange_ValidatesBoundsAndMaxPortCount(int start, int end, UdpPortRangeValidationError expected)
    {
        Assert.Equal(expected, LiveKitService.ValidateUdpMuxRange(start, end));
    }

    [Fact]
    public void BuildConfigurationYaml_DebugLevel_KeepsRootInfoAndSetsComponentDebugWithoutSdpFlood()
    {
        var systemConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitFallbackPort = 7881,
            LiveKitUdpStartPort = 30000,
            LiveKitUdpEndPort = 30007,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret",
            LiveKitLogLevel = "debug"
        };

        var yaml = _service.BuildConfigurationYaml(systemConfig);

        Assert.Contains("level: info", yaml);
        Assert.DoesNotContain("level: debug", yaml);
        Assert.Contains("pion_level: error", yaml);
        Assert.Contains("sample: true", yaml);
        Assert.Contains("room: debug", yaml);
        Assert.Contains("pub: debug", yaml);
        Assert.Contains("sub: debug", yaml);
        Assert.Contains("transport: debug", yaml);
        Assert.Contains("sfu: info", yaml);
        Assert.Contains("api: warn", yaml);
    }

    [Theory]
    [InlineData("warn", "level: warn")]
    [InlineData("error", "level: error")]
    public void BuildConfigurationYaml_WarnAndErrorLevels_EmitExpectedRootAndApiLevels(string level, string expectedLevelLine)
    {
        var systemConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitApiKey = "devkey",
            LiveKitApiSecret = "devsecret",
            LiveKitLogLevel = level
        };

        var yaml = _service.BuildConfigurationYaml(systemConfig);

        Assert.Contains(expectedLevelLine, yaml);
        Assert.Contains("pion_level: error", yaml);
        Assert.Contains("sample: true", yaml);
        Assert.Contains("api: error", yaml);
    }

    [Fact]
    public void BuildConfigurationYaml_WhenSetupComplete_PrefersSystemConfigOverEnvVar()
    {
        var configDict = new Dictionary<string, string?>
        {
            ["Spokes_LiveKit_Log_Level"] = "debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var service = new LiveKitService(_loggerMock.Object, configuration);

        var preSetupConfig = new SystemConfig
        {
            IsSetupComplete = false,
            LiveKitLogLevel = "info"
        };
        var preSetupYaml = service.BuildConfigurationYaml(preSetupConfig);
        Assert.Contains("room: debug", preSetupYaml);

        var postSetupConfig = new SystemConfig
        {
            IsSetupComplete = true,
            LiveKitLogLevel = "info"
        };
        var postSetupYaml = service.BuildConfigurationYaml(postSetupConfig);
        Assert.DoesNotContain("room: debug", postSetupYaml);
        Assert.Equal("channel-abc", LiveKitService.GetRoomName("abc"));
        Assert.Contains("level: info", postSetupYaml);
    }

    [Theory]
    [InlineData("twirp error unknown: participant does not exist", true)]
    [InlineData("requested participant not found", true)]
    [InlineData("twirp error not_found: requested room does not exist", true)]
    [InlineData("room not found", true)]
    [InlineData("could not find room channel-123", true)]
    [InlineData("Connection refused (127.0.0.1:7880)", false)]
    [InlineData("twirp error unauthenticated: invalid token", false)]
    public void IsParticipantOrRoomAbsentError_ClassifiesExceptionsAccurately(string message, bool expected)
    {
        var ex = new InvalidOperationException(message);
        Assert.Equal(expected, LiveKitService.IsParticipantOrRoomAbsentError(ex));

        var wrapped = new Exception("Outer wrapper", ex);
        Assert.Equal(expected, LiveKitService.IsParticipantOrRoomAbsentError(wrapped));
    }

    [Fact]
    public void IsParticipantOrRoomAbsentError_NullException_ReturnsFalse()
    {
        Assert.False(LiveKitService.IsParticipantOrRoomAbsentError(null));
    }

}
