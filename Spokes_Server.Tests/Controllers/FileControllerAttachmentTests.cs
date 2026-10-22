using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Controllers;

public class FileControllerAttachmentTests : TestDataTestBase
{
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly ChatChannelRepository _channelsRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly OpenIdAccountRepository _openIdAccountRepo;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly AlbumRepository _albums;
    private readonly DeviceSessionRepository _deviceSessions;
    private readonly UserService _userService;
    private readonly List<IFileAccessProvider> _providers;
    private readonly Employee _defaultUser;
    private readonly FileController _controller;
    private readonly IServiceProvider _serviceProvider;
    private readonly ScopedKeystoreService _keystore;
    private readonly CryptoService _defaultCrypto;

    public FileControllerAttachmentTests()
    {
        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        var writer = new DiskPersistenceService(mockLogger.Object);

        _channelsRepo = new ChatChannelRepository(writer, _mockConfig.Object);
        _employeeRepo = new EmployeeRepository(writer, _mockConfig.Object);
        _openIdAccountRepo = new OpenIdAccountRepository(writer, _mockConfig.Object);
        _companyProfiles = new CompanyProfileRepository(writer, _mockConfig.Object);
        _albums = new AlbumRepository(writer, _mockConfig.Object);
        _deviceSessions = new DeviceSessionRepository(writer, _mockConfig.Object);

        var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        _userService = new UserService(mockAuthStateProvider.Object, _employeeRepo, _openIdAccountRepo);

        // Setup DI services for DataProtection and Keystore
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_mockConfig.Object);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMemoryCache();
        _serviceProvider = services.BuildServiceProvider();

        var dataProtectionProvider = _serviceProvider.GetRequiredService<IDataProtectionProvider>();
        _keystore = new ScopedKeystoreService(dataProtectionProvider);
        _defaultCrypto = new CryptoService();

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
        _employeeRepo.Save(_defaultUser);
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-user1", Sub = "user1", LinkedEmployeeId = "user1" });

        // Seed other test employees
        _employeeRepo.Save(new Employee { Id = "user2", FirstName = "Second", LastName = "User", IsActive = true });
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-user2", Sub = "user2", LinkedEmployeeId = "user2" });

        _employeeRepo.Save(new Employee { Id = "admin1", FirstName = "Admin", LastName = "User", IsActive = true, IsAdmin = true });
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-admin1", Sub = "admin1", LinkedEmployeeId = "admin1" });

        _employeeRepo.Save(new Employee { Id = "inactive1", FirstName = "Inactive", LastName = "User", IsActive = false });
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-inactive1", Sub = "inactive1", LinkedEmployeeId = "inactive1" });

        _employeeRepo.Save(new Employee { Id = "suspended1", FirstName = "Suspended", LastName = "User", IsActive = true, IsSuspended = true });
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-suspended1", Sub = "suspended1", LinkedEmployeeId = "suspended1" });

        _employeeRepo.Save(new Employee { Id = "banned1", FirstName = "Banned", LastName = "User", IsActive = true, IsBanned = true });
        _openIdAccountRepo.Save(new OpenIdAccount { Id = "link-banned1", Sub = "banned1", LinkedEmployeeId = "banned1" });

        // Default chat provider setup
        var chatServiceMock = new Mock<IChatChannelAccessService>();
        chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
            .Returns((string uid) => _channelsRepo.GetDirectChannelsForUser(uid)
                .Concat(_channelsRepo.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

        var chatProvider = new ChatFileAccessProvider(chatServiceMock.Object);
        _providers = new List<IFileAccessProvider> { chatProvider, new EmailFileAccessProvider() };

        _controller = CreateController();
    }

    private FileController CreateController(
        IEnumerable<IFileAccessProvider>? providers = null,
        ClaimsPrincipal? principal = null,
        Dictionary<string, StringValues>? query = null,
        ICryptoService? crypto = null)
    {
        var activeCrypto = crypto ?? _defaultCrypto;
        var fileService = new FileService(_mockConfig.Object, NullLogger<FileService>.Instance, activeCrypto);
        var db = TestDatabaseFactory.Create(_deviceSessions, _employeeRepo);
        var escrow = new ServerEscrowService(db.CompanyProfile, db.ServerConfigs, activeCrypto);
        var imageService = new ImageProcessingService();
        var albumService = new AlbumService(_albums, _channelsRepo, activeCrypto, escrow, _employeeRepo);
        var keyResolvers = new IFileKeyResolver[]
        {
            new ChatFileKeyResolver(_channelsRepo, _keystore, activeCrypto),
            new AlbumFileKeyResolver(_albums, albumService, _keystore, activeCrypto)
        };
        var fileAuthService = new FileAuthorizationService(
            providers ?? _providers,
            keyResolvers);

        var controller = new FileController(
            fileService,
            fileAuthService,
            _companyProfiles,
            new Mock<ILogger<FileController>>().Object,
            _userService,
            imageService,
            _channelsRepo,
            config: _mockConfig.Object);

        var httpContext = new DefaultHttpContext
        {
            User = principal ?? CreateClaimsPrincipal(_defaultUser.Id),
            RequestServices = _serviceProvider
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
        var result = _controller.GetFile("chat", channelId!, "valid.png");
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
        var result = _controller.GetFile("chat", channelId, "valid.png");
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
        var result = _controller.GetFile("chat", channelId, "valid.png");
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
        var result = _controller.GetFile("chat", "chan1", fileName!);
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
        var result = _controller.GetFile("chat", "chan1", fileName);
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
        var result = _controller.GetFile("chat", "chan1", fileName);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    // =========================================================================
    // GetAttachment & Authentication / User State Validation
    // =========================================================================

    [Fact]
    public void GetAttachment_UserNotAuthenticated_ReturnsUnauthorized()
    {
        var unauthController = CreateController(principal: new ClaimsPrincipal(new ClaimsIdentity()));
        var result = unauthController.GetFile("chat", "chan1", "file.png");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_EmployeeNotFound_ReturnsUnauthorized()
    {
        var unknownUserController = CreateController(principal: CreateClaimsPrincipal("nonexistent-emp"));
        var result = unknownUserController.GetFile("chat", "chan1", "file.png");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_UserIsInactive_ReturnsUnauthorized()
    {
        var inactiveController = CreateController(principal: CreateClaimsPrincipal("inactive1"));
        var result = inactiveController.GetFile("chat", "chan1", "file.png");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_UserIsSuspended_ReturnsUnauthorized()
    {
        var suspendedController = CreateController(principal: CreateClaimsPrincipal("suspended1"));
        var result = suspendedController.GetFile("chat", "chan1", "file.png");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_UserIsBanned_ReturnsUnauthorized()
    {
        var bannedController = CreateController(principal: CreateClaimsPrincipal("banned1"));
        var result = bannedController.GetFile("chat", "chan1", "file.png");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    // =========================================================================
    // GetAttachment & Channel Validation / Authorization
    // =========================================================================

    [Fact]
    public void GetAttachment_ChannelDoesNotExist_ReturnsNotFound()
    {
        var result = _controller.GetFile("chat", "invalid-channel", "file.png");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Channel not found", spokesResult.ErrorMessage);
    }

    [Fact]
    public void GetAttachment_ChatProviderDeniesAccess_ReturnsForbid()
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
        mockProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), channel.Id)).Returns(false);

        var controller = CreateController(providers: new[] { mockProvider.Object });
        var result = controller.GetFile("chat", channel.Id, "file.png");
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_ChatProviderGrantsAccess_ReturnsPhysicalFile()
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
        mockProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), channel.Id)).Returns(true);

        var controller = CreateController(providers: new[] { mockProvider.Object });
        var result = controller.GetFile("chat", channel.Id, "doc.pdf");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
    }

    [Fact]
    public void GetAttachment_NoProviderAndUserNotParticipantInNonGeneralChannel_ReturnsForbid()
    {
        var channel = new ChatChannel
        {
            Id = "chan-no-provider-private",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user2", "user3" }
        };
        _channelsRepo.Save(channel);

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = controller.GetFile("chat", channel.Id, "file.png");
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachment_NoProviderAndUserIsParticipantInNonGeneralChannel_ReturnsPhysicalFile()
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
        var result = controller.GetFile("chat", channel.Id, "photo.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
    }

    [Fact]
    public void GetAttachment_NoProviderAndChannelIsGeneral_ReturnsPhysicalFile()
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
        var result = controller.GetFile("chat", channel.Id, "graphic.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    // =========================================================================
    // GetAttachment & File Serving (Inline vs Forced Download / MIME Types)
    // =========================================================================

    [Fact]
    public void GetAttachment_FileDoesNotExist_ReturnsNotFound()
    {
        var channel = new ChatChannel
        {
            Id = "chan-file-missing",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        var result = _controller.GetFile("chat", channel.Id, "missing.png");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData("image.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("vector.webp", "image/webp")]
    [InlineData("drawing.bmp", "image/bmp")]
    [InlineData("document.pdf", "application/pdf")]
    public void GetAttachment_SafeInlineContentType_ServesInline(string fileName, string expectedContentType)
    {
        var channel = new ChatChannel { Id = "chan-inline", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = controller.GetFile("chat", channel.Id, fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(expectedContentType, fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("inline;", disposition);
        Assert.Contains($"filename=\"{fileName}\"", disposition);
        Assert.Contains($"filename*=UTF-8''{Uri.EscapeDataString(fileName)}", disposition);
    }

    [Fact]
    public void GetAttachment_NonAsciiFileName_SetsAsciiSafeAndRfc5987ContentDisposition()
    {
        var channel = new ChatChannel { Id = "chan-unicode", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        var unicodeFileName = "résumé.pdf";
        CreateChannelAttachment(channel.Id, unicodeFileName);

        var controller = CreateController();
        var result = controller.GetFile("chat", channel.Id, unicodeFileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal("inline; filename=\"r_sum_.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", disposition);
    }

    [Fact]
    public void GetAttachment_DownloadQueryParamPresent_ForcesDownload()
    {
        var channel = new ChatChannel { Id = "chan-force-dl-query", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.jpg");

        var controller = CreateController(query: new Dictionary<string, StringValues> { ["download"] = "1" });
        var result = controller.GetFile("chat", channel.Id, "photo.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("photo.jpg", fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("vector.svg")]
    [InlineData("data.xml")]
    [InlineData("document.xhtml")]
    public void GetAttachment_ForceDownloadExtensions_ForcesDownload(string fileName)
    {
        var channel = new ChatChannel { Id = "chan-xss-extensions", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = controller.GetFile("chat", channel.Id, fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(fileName, fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("table.csv", "text/csv")]
    [InlineData("doc.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("archive.zip", "application/x-zip-compressed")]
    public void GetAttachment_UnsafeInlineContentType_ForcesDownload(string fileName, string expectedContentType)
    {
        var channel = new ChatChannel { Id = "chan-unsafe-inline", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, fileName);

        var controller = CreateController();
        var result = controller.GetFile("chat", channel.Id, fileName);
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
    public void GetAttachmentThumb_InvalidParameters_ReturnsBadRequest(string channelId, string fileName)
    {
        var result = _controller.GetFileThumb("chat", channelId, fileName);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    [Fact]
    public void GetAttachmentThumb_UserNotAuthorized_ReturnsForbid()
    {
        var channel = new ChatChannel
        {
            Id = "chan-thumb-forbid",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user2" }
        };
        _channelsRepo.Save(channel);

        var controller = CreateController(providers: Array.Empty<IFileAccessProvider>());
        var result = controller.GetFileThumb("chat", channel.Id, "image.png");
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetAttachmentThumb_ThumbFileExists_ReturnsThumbPhysicalFile()
    {
        var channel = new ChatChannel { Id = "chan-thumb-found", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.png_thumb.jpg", "thumb data");
        CreateChannelAttachment(channel.Id, "photo.png", "original data");

        var controller = CreateController();
        var result = controller.GetFileThumb("chat", channel.Id, "photo.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.EndsWith("photo.png_thumb.jpg", fileResult.FileName);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        Assert.Contains("filename=\"photo.png_thumb.jpg\"", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public void GetAttachmentThumb_ThumbDoesNotExistButOriginalExists_FallsBackToOriginal()
    {
        var channel = new ChatChannel { Id = "chan-thumb-fallback", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        CreateChannelAttachment(channel.Id, "photo.png", "original data");

        var controller = CreateController();
        var result = controller.GetFileThumb("chat", channel.Id, "photo.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.EndsWith("photo.png", fileResult.FileName);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        Assert.Contains("filename=\"photo.png\"", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public void GetAttachmentThumb_NeitherThumbNorOriginalExists_ReturnsNotFound()
    {
        var channel = new ChatChannel { Id = "chan-thumb-none", ChannelType = ChatChannelType.General };
        _channelsRepo.Save(channel);

        var result = _controller.GetFileThumb("chat", channel.Id, "missing.png");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // =========================================================================
    // GetEmailAttachment & Authentication / User State Validation
    // =========================================================================

    [Fact]
    public void GetEmailAttachment_UserNotAuthenticated_ReturnsUnauthorized()
    {
        var unauthController = CreateController(principal: new ClaimsPrincipal(new ClaimsIdentity()));
        var result = unauthController.GetEmailAttachment("msg1", "att1", "user1", null, null);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetEmailAttachment_UserIsInactive_ReturnsUnauthorized()
    {
        var inactiveController = CreateController(principal: CreateClaimsPrincipal("inactive1"));
        var result = inactiveController.GetEmailAttachment("msg1", "att1", "inactive1", null, null);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetEmailAttachment_UserIsSuspended_ReturnsUnauthorized()
    {
        var suspendedController = CreateController(principal: CreateClaimsPrincipal("suspended1"));
        var result = suspendedController.GetEmailAttachment("msg1", "att1", "suspended1", null, null);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetEmailAttachment_UserIsBanned_ReturnsUnauthorized()
    {
        var bannedController = CreateController(principal: CreateClaimsPrincipal("banned1"));
        var result = bannedController.GetEmailAttachment("msg1", "att1", "banned1", null, null);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    // =========================================================================
    // GetEmailAttachment & Parameter Validation / Path Traversal
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetEmailAttachment_EmployeeIdIsNullOrEmpty_ReturnsBadRequest(string? employeeId)
    {
        var result = _controller.GetEmailAttachment("msg1", "att1", employeeId!, null, null);
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
    public void GetEmailAttachment_MessageIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string messageId)
    {
        var result = _controller.GetEmailAttachment(messageId, "att1", "user1", null, null);
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
    public void GetEmailAttachment_AttachmentIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string attachmentId)
    {
        var result = _controller.GetEmailAttachment("msg1", attachmentId, "user1", null, null);
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
    public void GetEmailAttachment_EmployeeIdContainsPathTraversalOrInvalidChars_ReturnsBadRequest(string employeeId)
    {
        // Admin user to bypass the ownership check first
        var adminController = CreateController(principal: CreateClaimsPrincipal("admin1"));
        var result = adminController.GetEmailAttachment("msg1", "att1", employeeId, null, null);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Invalid path parameters", spokesResult.ErrorMessage);
    }

    // =========================================================================
    // GetEmailAttachment & Ownership / Admin Authorization
    // =========================================================================

    [Fact]
    public void GetEmailAttachment_NonAdminRequestsOtherEmployeeAttachment_ReturnsForbid()
    {
        // Default user is user1 (non-admin). Requesting user2's attachment
        var result = _controller.GetEmailAttachment("msg1", "att1", "user2", null, null);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetEmailAttachment_NonAdminRequestsOwnAttachment_ReturnsPhysicalFile()
    {
        CreateEmailAttachment("user1", "msg1", "att1.pdf");

        var result = _controller.GetEmailAttachment("msg1", "att1.pdf", "user1", null, null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
    }

    [Fact]
    public void GetEmailAttachment_AdminRequestsOtherEmployeeAttachment_ReturnsPhysicalFile()
    {
        CreateEmailAttachment("user2", "msg1", "att1.png");

        var adminController = CreateController(principal: CreateClaimsPrincipal("admin1"));
        var result = adminController.GetEmailAttachment("msg1", "att1.png", "user2", null, null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    // =========================================================================
    // GetEmailAttachment & File Serving, Content-Type, Filename, and Download Headers
    // =========================================================================

    [Fact]
    public void GetEmailAttachment_FileDoesNotExist_ReturnsNotFound()
    {
        var result = _controller.GetEmailAttachment("msg1", "missing.pdf", "user1", null, null);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetEmailAttachment_SafeInlineContentType_ServesInline()
    {
        CreateEmailAttachment("user1", "msg1", "photo_blob");

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", "photo_blob", "user1", ct: "image/png", fn: "avatar.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("image/png", fileResult.ContentType);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Equal("inline; filename=\"avatar.png\"; filename*=UTF-8''avatar.png", disposition);
    }

    [Fact]
    public void GetEmailAttachment_NonAsciiFileName_SetsAsciiSafeAndRfc5987ContentDisposition()
    {
        CreateEmailAttachment("user1", "msg1", "doc_blob");

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", "doc_blob", "user1", ct: "application/pdf", fn: "données.pdf");
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
    public void GetEmailAttachment_CtIsNullOrEmpty_FallsBackToExtensionContentType(string fileName, string expectedContentType)
    {
        CreateEmailAttachment("user1", "msg1", fileName);

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", fileName, "user1", ct: null, fn: fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(expectedContentType, fileResult.ContentType);
    }

    [Fact]
    public void GetEmailAttachment_FnIsNullOrEmpty_FallsBackToAttachmentIdAsFileName()
    {
        CreateEmailAttachment("user1", "msg1", "fallback_named_file.pdf");

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", "fallback_named_file.pdf", "user1", ct: null, fn: null);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("filename=\"fallback_named_file.pdf\"", disposition);
    }

    [Fact]
    public void GetEmailAttachment_DownloadQueryParamPresent_ForcesDownload()
    {
        CreateEmailAttachment("user1", "msg1", "image.png");

        var controller = CreateController(query: new Dictionary<string, StringValues> { ["download"] = "true" });
        var result = controller.GetEmailAttachment("msg1", "image.png", "user1", ct: "image/png", fn: "image.png");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("image.png", fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("script.html")]
    [InlineData("page.htm")]
    [InlineData("icon.svg")]
    [InlineData("feed.xml")]
    [InlineData("doc.xhtml")]
    public void GetEmailAttachment_ForceDownloadExtensions_ForcesDownload(string fileName)
    {
        CreateEmailAttachment("user1", "msg1", fileName);

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", fileName, "user1", ct: null, fn: fileName);
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal(fileName, fileResult.FileDownloadName);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    public void GetEmailAttachment_UnsafeContentType_ForcesDownload(string contentType)
    {
        CreateEmailAttachment("user1", "msg1", "data.bin");

        var controller = CreateController();
        var result = controller.GetEmailAttachment("msg1", "data.bin", "user1", ct: contentType, fn: "data.bin");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);

        Assert.Equal("data.bin", fileResult.FileDownloadName);
        Assert.Equal(contentType, fileResult.ContentType);
    }

    [Fact]
    public void GetAttachment_ModernPathFallback_ServesFile()
    {
        var channel = new ChatChannel
        {
            Id = "chan-modern-1",
            Name = "General",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        // Put file in modern Uploads/chat/chan-modern-1 instead of Attachments/chan-modern-1
        var dir = Path.Combine(_testDataPath, "Uploads", "chat", "chan-modern-1");
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, "modern-file.pdf");
        File.WriteAllText(fullPath, "modern-pdf-content");

        var result = _controller.GetFile("chat", "chan-modern-1", "modern-file.pdf");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(fullPath, fileResult.FileName);
    }

    [Fact]
    public void GetAttachment_EncryptedChannel_MissingKey_ReturnsBadRequest()
    {
        var channel = new ChatChannel
        {
            Id = "chan-enc-1",
            Name = "Encrypted",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        CreateChannelAttachment("chan-enc-1", "enc-file.pdf");

        var cryptoMock = new Mock<ICryptoService>();
        var controller = CreateController(crypto: cryptoMock.Object);

        var result = controller.GetFile("chat", "chan-enc-1", "enc-file.pdf");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var spokesResult = Assert.IsType<SpokesResult>(badRequest.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Contains("encryption key", spokesResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetLegacyAttachment_ServesAttachmentViaLegacyRoute()
    {
        var channel = new ChatChannel
        {
            Id = "chan-legacy-route",
            Name = "General",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        var fullPath = CreateChannelAttachment("chan-legacy-route", "legacy-pic.jpg");

        var result = _controller.GetLegacyAttachment("chan-legacy-route", "legacy-pic.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(fullPath, fileResult.FileName);
    }

    [Fact]
    public void GetLegacyAttachmentThumb_ServesThumbnailViaLegacyRoute()
    {
        var channel = new ChatChannel
        {
            Id = "chan-legacy-thumb",
            Name = "General",
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1" }
        };
        _channelsRepo.Save(channel);

        var thumbPath = CreateChannelAttachment("chan-legacy-thumb", "legacy-pic.jpg_thumb.jpg");

        var result = _controller.GetLegacyAttachmentThumb("chan-legacy-thumb", "legacy-pic.jpg");
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(thumbPath, fileResult.FileName);
    }
}
