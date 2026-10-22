namespace Spokes_Server.Tests.Controllers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Xunit;

public class SlideshowMediaControllerTests : IDisposable
{
    private readonly SlideshowSessionService _sessionService;
    private readonly SlideshowMediaController _controller;
    private readonly string _tempDir;

    public SlideshowMediaControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"slideshow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _sessionService = new SlideshowSessionService();
        _controller = new SlideshowMediaController(
            _sessionService,
            NullLogger<SlideshowMediaController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Theory]
    [InlineData("", "media-1")]
    [InlineData("sess-1", "")]
    [InlineData("  ", "  ")]
    public void GetMedia_InvalidParameters_ReturnsBadRequest(string sessionId, string mediaId)
    {
        var result = _controller.GetMedia(sessionId, mediaId);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetMedia_NonExistentSession_ReturnsForbidden403()
    {
        var result = _controller.GetMedia("non-existent-session", "media-1");
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public void GetMedia_UnpairedSession_ReturnsForbidden403()
    {
        var session = _sessionService.CreateSession("tv-conn-1");
        // Not paired yet
        var result = _controller.GetMedia(session.SessionId, "media-1");
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public void GetMedia_ActiveSessionWithoutBridge_ReturnsForbidden403()
    {
        var session = _sessionService.CreateSession("tv-conn-1");
        _sessionService.TryPair(session.PairingPin, "user-1", "host-1", "album-1", out _, out _, fileBridge: null);

        var result = _controller.GetMedia(session.SessionId, "media-1");
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public void GetMedia_ExpiredSession_ReturnsForbidden403()
    {
        var mockBridge = new Mock<ISlideshowFileBridge>();
        var session = _sessionService.CreateSession("tv-conn-1");
        _sessionService.TryPair(session.PairingPin, "user-1", "host-1", "album-1", out var pairedSession, out _, mockBridge.Object);

        // Force expiration
        pairedSession!.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5);

        var result = _controller.GetMedia(session.SessionId, "media-1");
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public void GetMedia_WhenSessionEnds_ImmediatelyReturnsForbidden403()
    {
        var mockBridge = new Mock<ISlideshowFileBridge>();
        mockBridge.Setup(b => b.GetMedia("media-1", false)).Returns(new OkResult());

        var session = _sessionService.CreateSession("tv-conn-1");
        _sessionService.TryPair(session.PairingPin, "user-1", "host-1", "album-1", out _, out _, mockBridge.Object);

        // Works while session is active
        var activeResult = _controller.GetMedia(session.SessionId, "media-1");
        Assert.IsType<OkResult>(activeResult);

        // Presenter ends the session
        _sessionService.EndSession(session.SessionId);

        // Immediate 403 Forbidden
        var endedResult = _controller.GetMedia(session.SessionId, "media-1");
        var objResult = Assert.IsType<ObjectResult>(endedResult);
        Assert.Equal(403, objResult.StatusCode);
    }

    [Fact]
    public void GetMedia_ActiveSessionWithBridge_DelegatesToBridge()
    {
        var mockBridge = new Mock<ISlideshowFileBridge>();
        var expectedResult = new PhysicalFileResult(Path.Combine(_tempDir, "photo.jpg"), "image/jpeg");
        mockBridge.Setup(b => b.GetMedia("photo-1", false)).Returns(expectedResult);

        var session = _sessionService.CreateSession("tv-conn-1");
        _sessionService.TryPair(session.PairingPin, "user-1", "host-1", "album-1", out _, out _, mockBridge.Object);

        var result = _controller.GetMedia(session.SessionId, "photo-1");

        Assert.Same(expectedResult, result);
        mockBridge.Verify(b => b.GetMedia("photo-1", false), Times.Once);
    }

    [Fact]
    public void GetMediaThumb_ActiveSessionWithBridge_DelegatesToBridgeWithThumbTrue()
    {
        var mockBridge = new Mock<ISlideshowFileBridge>();
        var expectedResult = new PhysicalFileResult(Path.Combine(_tempDir, "photo_thumb.jpg"), "image/jpeg");
        mockBridge.Setup(b => b.GetMedia("photo-1", true)).Returns(expectedResult);

        var session = _sessionService.CreateSession("tv-conn-1");
        _sessionService.TryPair(session.PairingPin, "user-1", "host-1", "album-1", out _, out _, mockBridge.Object);

        var result = _controller.GetMediaThumb(session.SessionId, "photo-1");

        Assert.Same(expectedResult, result);
        mockBridge.Verify(b => b.GetMedia("photo-1", true), Times.Once);
    }

    [Fact]
    public void UserScopedBridge_MediaNotInAlbum_ReturnsNotFound404()
    {
        var allowedMedia = new Dictionary<string, string>
        {
            { "media-1", "photo1.jpg" }
        };

        var user = new Employee { Id = "user-1", IsActive = true };
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var bridge = new UserScopedSlideshowBridge(serviceProvider, user, "album-1", null, allowedMedia);

        var result = bridge.GetMedia("unrelated-photo-id", thumb: false);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("Media item not found", notFound.Value?.ToString());
    }

    [Fact]
    public void UserScopedBridge_DelegatesToFileControllerServeFileForUser()
    {
        var fakeFilePath = Path.Combine(_tempDir, "photo1.jpg");
        File.WriteAllText(fakeFilePath, "image bytes");

        var user = new Employee { Id = "user-1", IsActive = true };
        var allowedMedia = new Dictionary<string, string>
        {
            { "media-1", "photo1.jpg" }
        };

        var mockFileService = new Mock<IFileService>();
        mockFileService.Setup(f => f.GetPhysicalPath("albums", "album-1", "photo1.jpg")).Returns(fakeFilePath);
        mockFileService.Setup(f => f.GetContentType("photo1.jpg")).Returns("image/jpeg");

        var mockAuthService = new Mock<IFileAuthorizationService>();
        mockAuthService.Setup(a => a.IsKnownCategory("albums")).Returns(true);
        mockAuthService.Setup(a => a.CanAccess("albums", "album-1", user)).Returns(true);
        mockAuthService.Setup(a => a.ResolveDownloadKey("albums", "album-1", user, null, null))
            .Returns(FileKeyResolutionResult.NotEncrypted());

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        var writer = new DiskPersistenceService(mockPersistenceLogger.Object);
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_tempDir);

        var empRepo = new EmployeeRepository(writer, mockConfig.Object);
        var openIdRepo = new OpenIdAccountRepository(writer, mockConfig.Object);
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, empRepo, openIdRepo);
        var companyProfileRepo = new CompanyProfileRepository(writer, mockConfig.Object);
        var chatChannelRepo = new ChatChannelRepository(writer, mockConfig.Object);

        var fileController = new FileController(
            mockFileService.Object,
            mockAuthService.Object,
            companyProfileRepo,
            NullLogger<FileController>.Instance,
            userService,
            new ImageProcessingService(),
            chatChannelRepo);

        var services = new ServiceCollection();
        services.AddSingleton(fileController);

        var sp = services.BuildServiceProvider();
        var bridge = new UserScopedSlideshowBridge(sp, user, "album-1", null, allowedMedia);

        var result = bridge.GetMedia("media-1", thumb: false);

        var physicalFile = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(fakeFilePath, physicalFile.FileName);
        Assert.Equal("image/jpeg", physicalFile.ContentType);
        Assert.True(physicalFile.EnableRangeProcessing);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
