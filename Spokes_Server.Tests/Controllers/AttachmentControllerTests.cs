using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Utilities;

namespace Spokes_Server.Tests.Controllers;

public class AttachmentControllerTests : TestDataTestBase
{
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly ChatChannelRepository _channelsRepo;
    private readonly UserService _userService;
    private readonly List<IFileAccessProvider> _providers;
    private readonly Employee _defaultUser;
    private readonly AttachmentController _controller;

    public AttachmentControllerTests()
    {
        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        var writer = new DiskPersistenceService(mockLogger.Object);

        _channelsRepo = new ChatChannelRepository(writer, _mockConfig.Object);
        var employeeRepo = new EmployeeRepository(writer, _mockConfig.Object);
        var openIdAccountRepo = new OpenIdAccountRepository(writer, _mockConfig.Object);

        var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        _userService = new UserService(mockAuthStateProvider.Object, employeeRepo, openIdAccountRepo);

        // Seed default active user (user1)
        _defaultUser = new Employee
        {
            Id = "user1",
            FirstName = "Test",
            LastName = "User",
            IsActive = true,
            IsSuspended = false,
            IsBanned = false,
            IsAdmin = false
        };
        employeeRepo.Save(_defaultUser);
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-user1", Sub = "user1", LinkedEmployeeId = "user1" });

        // Seed other test employees
        employeeRepo.Save(new Employee { Id = "user2", FirstName = "Second", LastName = "User", IsActive = true });
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-user2", Sub = "user2", LinkedEmployeeId = "user2" });

        employeeRepo.Save(new Employee { Id = "admin1", FirstName = "Admin", LastName = "User", IsActive = true, IsAdmin = true });
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-admin1", Sub = "admin1", LinkedEmployeeId = "admin1" });

        employeeRepo.Save(new Employee { Id = "inactive1", FirstName = "Inactive", LastName = "User", IsActive = false });
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-inactive1", Sub = "inactive1", LinkedEmployeeId = "inactive1" });

        employeeRepo.Save(new Employee { Id = "suspended1", FirstName = "Suspended", LastName = "User", IsActive = true, IsSuspended = true });
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-suspended1", Sub = "suspended1", LinkedEmployeeId = "suspended1" });

        employeeRepo.Save(new Employee { Id = "banned1", FirstName = "Banned", LastName = "User", IsActive = true, IsBanned = true });
        openIdAccountRepo.Save(new OpenIdAccount { Id = "link-banned1", Sub = "banned1", LinkedEmployeeId = "banned1" });

        // Default chat provider setup
        var chatServiceMock = new Mock<IChatChannelAccessService>();
        chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
            .Returns((string uid) => _channelsRepo.GetDirectChannelsForUser(uid)
                .Concat(_channelsRepo.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

        var chatProvider = new ChatFileAccessProvider(chatServiceMock.Object);
        _providers = new List<IFileAccessProvider> { chatProvider };

        _controller = CreateController();
    }

    private AttachmentController CreateController(
        IEnumerable<IFileAccessProvider>? providers = null,
        ClaimsPrincipal? principal = null,
        Dictionary<string, StringValues>? query = null)
    {
        var controller = new AttachmentController(
            _mockConfig.Object,
            _channelsRepo,
            providers ?? _providers,
            _userService);

        var httpContext = new DefaultHttpContext
        {
            User = principal ?? CreateClaimsPrincipal(_defaultUser.Id)
        };

        if (query != null && query.Count > 0)
        {
            httpContext.Request.Query = new QueryCollection(query);
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private static ClaimsPrincipal CreateClaimsPrincipal(string employeeId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, employeeId),
            new Claim("sub", employeeId),
            new Claim("EmployeeId", employeeId)
        }, "mock"));
    }

    private string CreateChannelAttachment(string channelId, string fileName, string content = "file-content")
    {
        var dir = Path.Combine(_testDataPath, "Attachments", channelId);
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private string CreateEmailAttachment(string employeeId, string messageId, string attachmentId, string content = "email-content")
    {
        var dir = Path.Combine(_testDataPath, "Employees", employeeId, "Email", "Attachments", messageId);
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, attachmentId);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // =========================================================================
    // GetAttachment & Path Parameter Validation (Path Traversal Rejection)
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAttachment_InvalidChannelId_ReturnsBadRequest(string? channelId)
    {
        var result = await _controller.GetAttachment(channelId!, "valid.png");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("chan/..")]
    [InlineData("chan..nel")]
    public async Task GetAttachment_ChannelIdContainsDotDot_ReturnsBadRequest(string channelId)
    {
        var result = await _controller.GetAttachment(channelId, "valid.png");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("chan\0nel")]
    [InlineData("chan:nel")]
    [InlineData("chan<nel")]
    public async Task GetAttachment_ChannelIdContainsInvalidChars_ReturnsBadRequest(string channelId)
    {
        var result = await _controller.GetAttachment(channelId, "valid.png");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAttachment_InvalidFileName_ReturnsBadRequest(string? fileName)
    {
        var result = await _controller.GetAttachment("chan1", fileName!);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("dir/../file.png")]
    [InlineData("file..png")]
    public async Task GetAttachment_FileNameContainsDotDot_ReturnsBadRequest(string fileName)
    {
        var result = await _controller.GetAttachment("chan1", fileName);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("file\0name.png")]
    [InlineData("file:name.png")]
    [InlineData("file<name.png")]
    public async Task GetAttachment_FileNameContainsInvalidChars_ReturnsBadRequest(string fileName)
    {
        var result = await _controller.GetAttachment("chan1", fileName);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    // =========================================================================
    // GetAttachment & Authentication / User State Validation
    // =========================================================================

    [Fact]
    public async Task GetAttachment_UserNotAuthenticated_ReturnsUnauthorized()
    {
        var unauthController = CreateController(principal: new ClaimsPrincipal(new ClaimsIdentity()));
        var result = await unauthController.GetAttachment("chan1", "file.png");
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetAttachment_EmployeeNotFound_ReturnsUnauthorized()
    {
        var unknownUserController = CreateController(principal: CreateClaimsPrincipal("nonexistent-emp"));
        var result = await unknownUserController.GetAttachment("chan1", "file.png");
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetAttachment_UserIsInactive_ReturnsUnauthorized()
    {
        var inactiveController = CreateController(principal: CreateClaimsPrincipal("inactive1"));
        var result = await inactiveController.GetAttachment("chan1", "file.png");
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetAttachment_UserIsSuspended_ReturnsUnauthorized()
    {
        var suspendedController = CreateController(principal: CreateClaimsPrincipal("suspended1"));
        var result = await suspendedController.GetAttachment("chan1", "file.png");
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetAttachment_UserIsBanned_ReturnsUnauthorized()
    {
        var bannedController = CreateController(principal: CreateClaimsPrincipal("banned1"));
        var result = await bannedController.GetAttachment("chan1", "file.png");
        Assert.IsType<UnauthorizedResult>(result);
    }

    // =========================================================================
    // GetAttachment & Channel Validation / Authorization
    // =========================================================================

    [Fact]
    public async Task GetAttachment_ChannelDoesNotExist_ReturnsNotFound()
    {
        var result = await _controller.GetAttachment("invalid-channel", "file.png");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Channel not found", spokesResult.ErrorMessage);
    }

    [Fact]
    public async Task GetAttachment_ChatProviderDeniesAccess_ReturnsForbid()
    {
        var channel = new ChatChannel
        {
            Id = "chan-provider-deny",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user2", "user3" }
        };
        _channelsRepo.Save(channel);

        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("chat");
        mockProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), channel.Id)).ReturnsAsync(false);

        var controller = CreateController(providers: new[] { mockProvider.Object });
        var result = await controller.GetAttachment(channel.Id, "file.png");
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAttachment_ChatProviderGrantsAccess_ReturnsPhysicalFile()
    {
        var channel = new ChatChannel
        {
            Id = "chan-provider-allow",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "doc.pdf");

        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("chat");
        mockProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), channel.Id)).ReturnsAsync(true);

        var controller = CreateController(providers: new[] { mockProvider.Object });
        var result = await controller.GetAttachment(channel.Id, "doc.pdf");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
    }

    [Fact]
    public async Task GetAttachment_NoProviderAndUserNotParticipantInNonGeneralChannel_ReturnsForbid()
    {
        var channel = new ChatChannel
        {
            Id = "chan-no-provider-private",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user2", "user3" }
        };
        _channelsRepo.Save(channel);

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = await controller.GetAttachment(channel.Id, "file.png");
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAttachment_NoProviderAndUserIsParticipantInNonGeneralChannel_ReturnsPhysicalFile()
    {
        var channel = new ChatChannel
        {
            Id = "chan-no-provider-allowed",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.jpg");

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = await controller.GetAttachment(channel.Id, "photo.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
    }

    [Fact]
    public async Task GetAttachment_NoProviderAndChannelIsGeneral_ReturnsPhysicalFile()
    {
        var channel = new ChatChannel
        {
            Id = "chan-general-fallback",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user2" } // user1 is not in list
        };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "graphic.png");

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = await controller.GetAttachment(channel.Id, "graphic.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    // =========================================================================
    // GetAttachment & File Serving (Inline vs Forced Download / MIME Types)
    // =========================================================================

    [Fact]
    public async Task GetAttachment_FileDoesNotExist_ReturnsNotFound()
    {
        var channel = new ChatChannel
        {
            Id = "chan-file-missing",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        var result = await _controller.GetAttachment(channel.Id, "missing.png");
        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("image.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("vector.webp", "image/webp")]
    [InlineData("drawing.bmp", "image/bmp")]
    [InlineData("document.pdf", "application/pdf")]
    public async Task GetAttachment_SafeInlineContentType_ServesInline(string fileName, string expectedContentType)
    {
        var channel = new ChatChannel { Id = "chan-inline", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = await controller.GetAttachment(channel.Id, fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(expectedContentType, fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("inline;", disposition);
        Assert.Contains($"filename=\"{fileName}\"", disposition);
        Assert.Contains($"filename*=UTF-8''{Uri.EscapeDataString(fileName)}", disposition);
    }

    [Fact]
    public async Task GetAttachment_NonAsciiFileName_SetsAsciiSafeAndRfc5987ContentDisposition()
    {
        var channel = new ChatChannel { Id = "chan-unicode", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        var unicodeFileName = "résumé.pdf";
        CreateChannelAttachment(channel.Id, unicodeFileName);

        var controller = CreateController();
        var result = await controller.GetAttachment(channel.Id, unicodeFileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal("inline; filename=\"r_sum_.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", disposition);
    }

    [Fact]
    public async Task GetAttachment_DownloadQueryParamPresent_ForcesDownload()
    {
        var channel = new ChatChannel { Id = "chan-force-dl-query", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.jpg");

        var controller = CreateController(query: new Dictionary<string, StringValues> { ["download"] = "1" });
        var result = await controller.GetAttachment(channel.Id, "photo.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("photo.jpg", fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("vector.svg")]
    [InlineData("data.xml")]
    [InlineData("document.xhtml")]
    public async Task GetAttachment_ForceDownloadExtensions_ForcesDownload(string fileName)
    {
        var channel = new ChatChannel { Id = "chan-xss-extensions", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = await controller.GetAttachment(channel.Id, fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(fileName, fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("table.csv", "text/csv")]
    [InlineData("doc.docx", "application/vnd.ms-word")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("archive.zip", "application/octet-stream")]
    public async Task GetAttachment_UnsafeInlineContentType_ForcesDownload(string fileName, string expectedContentType)
    {
        var channel = new ChatChannel { Id = "chan-unsafe-inline", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = await controller.GetAttachment(channel.Id, fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(expectedContentType, fileResult.ContentType);
        Assert.Equal(fileName, fileResult.FileDownloadName);
    }

    // =========================================================================
    // GetAttachmentThumb Scenarios
    // =========================================================================

    [Theory]
    [InlineData("..", "image.png")]
    [InlineData("chan1", "..")]
    [InlineData("chan\0nel", "image.png")]
    [InlineData("chan1", "image\0.png")]
    [InlineData("", "image.png")]
    [InlineData("chan1", "")]
    public async Task GetAttachmentThumb_InvalidParameters_ReturnsBadRequest(string channelId, string fileName)
    {
        var result = await _controller.GetAttachmentThumb(channelId, fileName);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Fact]
    public async Task GetAttachmentThumb_UserNotAuthorized_ReturnsForbid()
    {
        var channel = new ChatChannel
        {
            Id = "chan-thumb-forbid",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user2" }
        };
        _channelsRepo.Save(channel);

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = await controller.GetAttachmentThumb(channel.Id, "image.png");
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAttachmentThumb_ThumbFileExists_ReturnsThumbPhysicalFile()
    {
        var channel = new ChatChannel { Id = "chan-thumb-found", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.png_thumb.jpg", "thumb data");
        CreateChannelAttachment(channel.Id, "photo.png", "original data");

        var controller = CreateController();
        var result = await controller.GetAttachmentThumb(channel.Id, "photo.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.EndsWith("photo.png_thumb.jpg", fileResult.FileName);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        Assert.Contains("filename=\"photo.png_thumb.jpg\"", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task GetAttachmentThumb_ThumbDoesNotExistButOriginalExists_FallsBackToOriginal()
    {
        var channel = new ChatChannel { Id = "chan-thumb-fallback", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.png", "original data");

        var controller = CreateController();
        var result = await controller.GetAttachmentThumb(channel.Id, "photo.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.EndsWith("photo.png", fileResult.FileName);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        Assert.Contains("filename=\"photo.png\"", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task GetAttachmentThumb_NeitherThumbNorOriginalExists_ReturnsNotFound()
    {
        var channel = new ChatChannel { Id = "chan-thumb-none", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        var result = await _controller.GetAttachmentThumb(channel.Id, "missing.png");
        Assert.IsType<NotFoundResult>(result);
    }

    // =========================================================================
    // GetEmailAttachment & Authentication / User State Validation
    // =========================================================================

    [Fact]
    public async Task GetEmailAttachment_UserNotAuthenticated_ReturnsUnauthorized()
    {
        var unauthController = CreateController(principal: new ClaimsPrincipal(new ClaimsIdentity()));
        var result = await unauthController.GetEmailAttachment("msg1", "att1", "user1", null, null);
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetEmailAttachment_UserIsInactive_ReturnsUnauthorized()
    {
        var inactiveController = CreateController(principal: CreateClaimsPrincipal("inactive1"));
        var result = await inactiveController.GetEmailAttachment("msg1", "att1", "inactive1", null, null);
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetEmailAttachment_UserIsSuspended_ReturnsUnauthorized()
    {
        var suspendedController = CreateController(principal: CreateClaimsPrincipal("suspended1"));
        var result = await suspendedController.GetEmailAttachment("msg1", "att1", "suspended1", null, null);
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetEmailAttachment_UserIsBanned_ReturnsUnauthorized()
    {
        var bannedController = CreateController(principal: CreateClaimsPrincipal("banned1"));
        var result = await bannedController.GetEmailAttachment("msg1", "att1", "banned1", null, null);
        Assert.IsType<UnauthorizedResult>(result);
    }

    // =========================================================================
    // GetEmailAttachment & Parameter Validation / Path Traversal
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetEmailAttachment_EmployeeIdIsNullOrEmpty_ReturnsBadRequest(string? employeeId)
    {
        var result = await _controller.GetEmailAttachment("msg1", "att1", employeeId!, null, null);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("employeeId is required", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("msg/sub")]
    [InlineData("msg\\sub")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEmailAttachment_MessageIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string messageId)
    {
        var result = await _controller.GetEmailAttachment(messageId, "att1", "user1", null, null);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("att/sub")]
    [InlineData("att\\sub")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEmailAttachment_AttachmentIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string attachmentId)
    {
        var result = await _controller.GetEmailAttachment("msg1", attachmentId, "user1", null, null);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../user1")]
    [InlineData("user1/sub")]
    [InlineData("user1\\sub")]
    public async Task GetEmailAttachment_EmployeeIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string employeeId)
    {
        // Admin user to bypass the ownership check first
        var adminController = CreateController(principal: CreateClaimsPrincipal("admin1"));
        var result = await adminController.GetEmailAttachment("msg1", "att1", employeeId, null, null);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    // =========================================================================
    // GetEmailAttachment & Ownership / Admin Authorization
    // =========================================================================

    [Fact]
    public async Task GetEmailAttachment_NonAdminRequestsOtherEmployeeAttachment_ReturnsForbid()
    {
        // Default user is user1 (non-admin). Requesting user2's attachment
        var result = await _controller.GetEmailAttachment("msg1", "att1", "user2", null, null);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetEmailAttachment_NonAdminRequestsOwnAttachment_ReturnsPhysicalFile()
    {
        CreateEmailAttachment("user1", "msg1", "att1.pdf");

        var result = await _controller.GetEmailAttachment("msg1", "att1.pdf", "user1", null, null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
    }

    [Fact]
    public async Task GetEmailAttachment_AdminRequestsOtherEmployeeAttachment_ReturnsPhysicalFile()
    {
        CreateEmailAttachment("user2", "msg1", "att1.png");

        var adminController = CreateController(principal: CreateClaimsPrincipal("admin1"));
        var result = await adminController.GetEmailAttachment("msg1", "att1.png", "user2", null, null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    // =========================================================================
    // GetEmailAttachment & File Serving, Content-Type, Filename, and Download Headers
    // =========================================================================

    [Fact]
    public async Task GetEmailAttachment_FileDoesNotExist_ReturnsNotFound()
    {
        var result = await _controller.GetEmailAttachment("msg1", "missing.pdf", "user1", null, null);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetEmailAttachment_SafeInlineContentType_ServesInline()
    {
        CreateEmailAttachment("user1", "msg1", "photo_blob");

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", "photo_blob", "user1", ct: "image/png", fn: "avatar.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("image/png", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal("inline; filename=\"avatar.png\"; filename*=UTF-8''avatar.png", disposition);
    }

    [Fact]
    public async Task GetEmailAttachment_NonAsciiFileName_SetsAsciiSafeAndRfc5987ContentDisposition()
    {
        CreateEmailAttachment("user1", "msg1", "doc_blob");

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", "doc_blob", "user1", ct: "application/pdf", fn: "données.pdf");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal("inline; filename=\"donn_es.pdf\"; filename*=UTF-8''donn%C3%A9es.pdf", disposition);
    }

    [Theory]
    [InlineData("file.pdf", "application/pdf")]
    [InlineData("file.png", "image/png")]
    [InlineData("file.jpg", "image/jpeg")]
    [InlineData("file.txt", "text/plain")]
    [InlineData("file.unknown", "application/octet-stream")]
    public async Task GetEmailAttachment_CtIsNullOrEmpty_FallsBackToExtensionContentType(string fileName, string expectedContentType)
    {
        CreateEmailAttachment("user1", "msg1", fileName);

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", fileName, "user1", ct: null, fn: fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(expectedContentType, fileResult.ContentType);
    }

    [Fact]
    public async Task GetEmailAttachment_FnIsNullOrEmpty_FallsBackToAttachmentIdAsFileName()
    {
        CreateEmailAttachment("user1", "msg1", "fallback_named_file.pdf");

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", "fallback_named_file.pdf", "user1", ct: null, fn: null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("filename=\"fallback_named_file.pdf\"", disposition);
    }

    [Fact]
    public async Task GetEmailAttachment_DownloadQueryParamPresent_ForcesDownload()
    {
        CreateEmailAttachment("user1", "msg1", "image.png");

        var controller = CreateController(query: new Dictionary<string, StringValues> { ["download"] = "true" });
        var result = await controller.GetEmailAttachment("msg1", "image.png", "user1", ct: "image/png", fn: "image.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("image.png", fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("script.html")]
    [InlineData("page.htm")]
    [InlineData("icon.svg")]
    [InlineData("feed.xml")]
    [InlineData("doc.xhtml")]
    public async Task GetEmailAttachment_ForceDownloadExtensions_ForcesDownload(string fileName)
    {
        CreateEmailAttachment("user1", "msg1", fileName);

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", fileName, "user1", ct: null, fn: fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(fileName, fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    public async Task GetEmailAttachment_UnsafeContentType_ForcesDownload(string contentType)
    {
        CreateEmailAttachment("user1", "msg1", "data.bin");

        var controller = CreateController();
        var result = await controller.GetEmailAttachment("msg1", "data.bin", "user1", ct: contentType, fn: "data.bin");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("data.bin", fileResult.FileDownloadName);
        Assert.Equal(contentType, fileResult.ContentType);
    }
}
