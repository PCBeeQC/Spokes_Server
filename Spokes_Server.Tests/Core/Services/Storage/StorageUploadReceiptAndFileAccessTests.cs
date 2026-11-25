using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Components.Shared;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Storage;

public class StorageUploadReceiptAndFileAccessTests : TestDataTestBase
{
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _writer;
    private readonly EmployeeRepository _employees;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly FileTokenService _fileTokens;
    private readonly Mock<ICryptoService> _mockCrypto;

    public StorageUploadReceiptAndFileAccessTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDataPath , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"})
            .Build();
        _writer = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _employees = new EmployeeRepository(_writer, _config);
        _openIdAccounts = new OpenIdAccountRepository(_writer, _config);
        _systemConfigs = new SystemConfigRepository(_writer, _config);
        _fileTokens = new FileTokenService(
            new EphemeralDataProtectionProvider(),
            new MemoryCache(new MemoryCacheOptions()),
            _systemConfigs);
        _mockCrypto = new Mock<ICryptoService>();
    }

    private UserService CreateUserService(string sub, Employee employee)
    {
        _employees.Save(employee);
        _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = employee.Id });

        var identity = new ClaimsIdentity([new Claim("sub", sub)], "TestAuth");
        var authState = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        var mockAuthProvider = new Mock<AuthenticationStateProvider>();
        mockAuthProvider.Setup(a => a.GetAuthenticationStateAsync()).Returns(authState);

        return new UserService(mockAuthProvider.Object, _employees, _openIdAccounts);
    }

    [Fact]
    public void FileController_ServeFileForUserAsync_IsNonActionAndInternal()
    {
        var method = typeof(FileController).GetMethod(
            "ServeFileForUserAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.False(method!.IsPublic);
        Assert.True(method.IsAssembly);
        Assert.NotNull(method.GetCustomAttribute<NonActionAttribute>());
    }

    [Fact]
    public void FileTokenService_UploadReceipt_ValidatesAndEnforcesScopeAndSingleUse()
    {
        var token = _fileTokens.GenerateUploadReceiptToken("albums", "alb-1", "photo_1.jpg", "emp-1");

        Assert.True(_fileTokens.TryValidateUploadReceiptToken(token, "albums", "alb-1", "emp-1", out var safeName));
        Assert.Equal("photo_1.jpg", safeName);

        Assert.False(_fileTokens.TryValidateUploadReceiptToken(token, "chat", "alb-1", "emp-1", out _));
        Assert.False(_fileTokens.TryValidateUploadReceiptToken(token, "albums", "alb-2", "emp-1", out _));
        Assert.False(_fileTokens.TryValidateUploadReceiptToken(token, "albums", "alb-1", "emp-2", out _));

        Assert.Throws<ArgumentException>(() => _fileTokens.GenerateUploadReceiptToken("albums", "../evil", "a.jpg", "emp-1"));
        Assert.Throws<ArgumentException>(() => _fileTokens.GenerateUploadReceiptToken("albums", "alb-1", "../a.jpg", "emp-1"));

        var tempToken = _fileTokens.GenerateUploadReceiptToken("temp", "ctx-1", "logo.png", "emp-1");
        Assert.True(_fileTokens.TryValidateUploadReceiptToken(tempToken, "temp", "ctx-1", "emp-1", out _, consumeSingleUse: true));
        Assert.False(_fileTokens.TryValidateUploadReceiptToken(tempToken, "temp", "ctx-1", "emp-1", out _, consumeSingleUse: true));
    }

    [Fact]
    public async Task UploadCompletionVerifier_RewritesForgedPath_AndRejectsInvalidTokensOrSuspendedUsers()
    {
        var fileService = new FileService(_config, NullLogger<FileService>.Instance, _mockCrypto.Object);
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes")))
        {
            await fileService.UploadStreamAsync("albums", "alb-1", ms, "photo.jpg", predefinedSafeName: "photo_safe.jpg");
        }

        var emp = new Employee { Id = "emp-1", IsActive = true, IsSuspended = false };
        var userService = CreateUserService("sub-1", emp);

        var mockFileAuth = new Mock<IFileAuthorizationService>();
        mockFileAuth.Setup(a => a.IsKnownCategory(It.IsAny<string>())).Returns(true);
        mockFileAuth.Setup(a => a.CanUpload("albums", "alb-1", It.Is<Employee>(e => e.Id == "emp-1"))).Returns(true);

        var cfg = _systemConfigs.Get();
        cfg.IsSetupComplete = true;
        _systemConfigs.Save(cfg);

        var verifier = new UploadCompletionVerifier(
            userService,
            mockFileAuth.Object,
            _fileTokens,
            fileService,
            _systemConfigs,
            NullLogger<UploadCompletionVerifier>.Instance);

        var validReceipt = _fileTokens.GenerateUploadReceiptToken("albums", "alb-1", "photo_safe.jpg", "emp-1");
        var forgedInput = new ApiFileUpload.UploadResult
        {
            FilePath = "/spokesapi/files/chat/secret-channel/hijack.jpg",
            Category = "chat",
            ContextId = "secret-channel",
            UploadToken = validReceipt
        };

        var validRes = await verifier.VerifyAndNormalizeAsync([forgedInput], "albums", "alb-1");
        Assert.True(validRes.IsSuccess);
        Assert.Single(validRes.VerifiedResults);
        Assert.Equal("/spokesapi/files/albums/alb-1/photo_safe.jpg", validRes.VerifiedResults[0].FilePath);
        Assert.Equal("albums", validRes.VerifiedResults[0].Category);
        Assert.Equal("alb-1", validRes.VerifiedResults[0].ContextId);

        // Missing/forged receipt rejected
        var missingTokenRes = await verifier.VerifyAndNormalizeAsync(
            [new ApiFileUpload.UploadResult { FilePath = "/spokesapi/files/albums/alb-1/photo_safe.jpg", UploadToken = "forged" }],
            "albums",
            "alb-1");
        Assert.False(missingTokenRes.IsSuccess);
        Assert.Empty(missingTokenRes.VerifiedResults);

        // Missing file on disk rejected
        var missingFileToken = _fileTokens.GenerateUploadReceiptToken("albums", "alb-1", "missing.jpg", "emp-1");
        var missingFileRes = await verifier.VerifyAndNormalizeAsync(
            [new ApiFileUpload.UploadResult { UploadToken = missingFileToken }],
            "albums",
            "alb-1");
        Assert.False(missingFileRes.IsSuccess);

        // Suspended user rejected even with valid token
        emp.IsSuspended = true;
        _employees.Save(emp);
        var suspendedRes = await verifier.VerifyAndNormalizeAsync(
            [new ApiFileUpload.UploadResult { UploadToken = validReceipt }],
            "albums",
            "alb-1");
        Assert.False(suspendedRes.IsSuccess);
    }

    [Fact]
    public async Task UploadCompletionVerifier_AnonymousSetupCallbackWithoutReceipt_IsAlwaysRejected()
    {
        var fileService = new FileService(_config, NullLogger<FileService>.Instance, _mockCrypto.Object);
        var mockAuthProvider = new Mock<AuthenticationStateProvider>();
        mockAuthProvider.Setup(a => a.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        var anonUserService = new UserService(mockAuthProvider.Object, _employees, _openIdAccounts);
        var mockFileAuth = new Mock<IFileAuthorizationService>();

        var cfg = _systemConfigs.Get();
        cfg.IsSetupComplete = false;
        _systemConfigs.Save(cfg);

        var verifier = new UploadCompletionVerifier(
            anonUserService,
            mockFileAuth.Object,
            _fileTokens,
            fileService,
            _systemConfigs,
            NullLogger<UploadCompletionVerifier>.Instance);

        // Pre-setup backup restores use the dedicated chunked /setup/api/restore-upload endpoints;
        // UploadCompletionVerifier must never bypass authentication/receipt checks.
        var preSetup = await verifier.VerifyAndNormalizeAsync(
            [new ApiFileUpload.UploadResult { FilePath = "/spokesapi/files/setup/restore/backup.spkbackup" }],
            "setup",
            "restore",
            "/setup/api/restore-upload/init");
        Assert.False(preSetup.IsSuccess);

        cfg.IsSetupComplete = true;
        _systemConfigs.Save(cfg);

        var postSetup = await verifier.VerifyAndNormalizeAsync(
            [new ApiFileUpload.UploadResult { FilePath = "/spokesapi/files/setup/restore/backup.spkbackup" }],
            "setup",
            "restore",
            "/setup/api/restore-upload/init");
        Assert.False(postSetup.IsSuccess);
    }

    [Fact]
    public void AlbumAndAlbumService_ConfineMediaPaths_AndClearInitialMedia()
    {
        var albumRepo = new AlbumRepository(_writer, _config);
        var channelRepo = new ChatChannelRepository(_writer, _config);
        var db = TestDatabaseFactory.Create();
        var escrow = new ServerEscrowService(db.CompanyProfile, db.ServerConfigs, _mockCrypto.Object);

        _employees.Save(new Employee { Id = "u1", IsActive = true, Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use] });
        _mockCrypto.Setup(c => c.GenerateAesKeyBase64()).Returns(Convert.ToBase64String(new byte[32]));

        var service = new AlbumService(albumRepo, channelRepo, _mockCrypto.Object, escrow, _employees, new ChatAuthorizationService(channelRepo, new Spokes_Server.Core.Data.Repositories.Projects.ProjectRepository(_writer, _config), new Spokes_Server.Core.Data.Repositories.HR.TeamRepository(_writer, _config), _employees));

        var smuggled = new Album
        {
            Id = "alb-clean",
            Title = "Cleaned",
            Media = [new AlbumMedia { Id = "smuggled-1", FilePath = "/spokesapi/files/chat/c1/victim.jpg" }]
        };
        var createRes = service.CreateAlbum(smuggled, "u1");
        Assert.True(createRes.IsSuccess);
        Assert.Empty(albumRepo.GetById("alb-clean")!.Media);

        // Foreign / traversal paths rejected
        Assert.False(service.AddMedia("alb-clean", "u1",
            [new AlbumMedia { Id = "bad-1", FilePath = "/spokesapi/files/chat/c1/victim.jpg", AddedByUserId = "victim" }]).IsSuccess);
        Assert.False(service.AddMedia("alb-clean", "u1",
            [new AlbumMedia { Id = "bad-2", FilePath = "/spokesapi/files/albums/alb-clean/../evil.jpg" }]).IsSuccess);
        Assert.Empty(albumRepo.GetById("alb-clean")!.Media);

        // Valid path accepted & AddedByUserId overwritten
        var okRes = service.AddMedia("alb-clean", "u1",
            [new AlbumMedia { Id = "ok-1", FilePath = "/spokesapi/files/albums/alb-clean/ok.jpg?token=abc", AddedByUserId = "spoofed" }]);
        Assert.True(okRes.IsSuccess);
        var persisted = albumRepo.GetById("alb-clean")!;
        Assert.Single(persisted.Media);
        Assert.Equal("/spokesapi/files/albums/alb-clean/ok.jpg", persisted.Media[0].FilePath);
        Assert.Equal("u1", persisted.Media[0].AddedByUserId);

        // Duplicate rejected
        Assert.False(service.AddMedia("alb-clean", "u1",
            [new AlbumMedia { Id = "ok-dup", FilePath = "/spokesapi/files/albums/alb-clean/ok.jpg" }]).IsSuccess);

        // Album.GetAssociatedFilePaths filters out any tampered non-album paths
        var mixedAlbum = new Album
        {
            Id = "alb-paths",
            Media =
            [
                new AlbumMedia { FilePath = "/spokesapi/files/albums/alb-paths/a.jpg?t=1" },
                new AlbumMedia { FilePath = "/spokesapi/files/chat/chan-1/secret.jpg" },
                new AlbumMedia { FilePath = "/spokesapi/files/albums/other/b.jpg" },
                new AlbumMedia { FilePath = "/spokesapi/files/albums/alb-paths/../escape.jpg" }
            ]
        };
        var associated = Assert.Single(mixedAlbum.GetAssociatedFilePaths());
        Assert.Equal("/spokesapi/files/albums/alb-paths/a.jpg?t=1", associated);
    }

    [Fact]
    public async Task AlbumService_ImportChatAttachments_SkipsSourceDeleteWhenReferencedByAnotherActiveMessage()
    {
        var realCrypto = new CryptoService();
        var fileService = new FileService(_config, NullLogger<FileService>.Instance, realCrypto);
        var albumRepo = new AlbumRepository(_writer, _config);
        var channelRepo = new ChatChannelRepository(_writer, _config);
        var db = TestDatabaseFactory.Create();
        var chatMessages = new ChatMessageRepository(_writer, _config, db.CompanyProfile);
        var escrow = new ServerEscrowService(db.CompanyProfile, db.ServerConfigs, realCrypto);

        var (pubKey, privKey) = realCrypto.GenerateRsaKeyPair();
        _employees.Save(new Employee { Id = "u1", PublicKey = pubKey, EncryptedPrivateKey = "enc-priv", IsActive = true, Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use] });
        _employees.Save(new Employee { Id = "u2", IsActive = true, Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use] });
        var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsEncrypted = false };
        channelRepo.Save(channel);

        string uploadedUrl;
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("chat-photo-bytes")))
        {
            uploadedUrl = await fileService.UploadStreamAsync("chat", "chan-1", ms, "shared.jpg", predefinedSafeName: "shared_safe.jpg");
        }

        // Another user's non-deleted message still references the same chat file
        chatMessages.Save(new ChatMessage
        {
            Id = "msg-other",
            ChannelId = "chan-1",
            SenderId = "u2",
            IsDeleted = false,
            Attachments = [new ChatAttachment { Id = "att-2", FilePath = uploadedUrl, ContentType = "image/jpeg" }]
        });

        var service = new AlbumService(
            albumRepo,
            channelRepo,
            realCrypto,
            escrow,
            _employees,
            new ChatAuthorizationService(channelRepo, new Spokes_Server.Core.Data.Repositories.Projects.ProjectRepository(_writer, _config), new Spokes_Server.Core.Data.Repositories.HR.TeamRepository(_writer, _config), _employees),
            fileService: fileService,
            chatMessages: chatMessages);

        var res = await service.ImportChatAttachmentsToAlbumAsync(
            "u1",
            "chan-1",
            [new ChatAttachment { Id = "att-1", FileName = "shared.jpg", FilePath = uploadedUrl, ContentType = "image/jpeg" }],
            targetAlbumId: null,
            newAlbumTitle: "Shared Chat Photos",
            currentPrivateKey: privKey,
            deleteSourceFiles: true,
            sourceMessageId: "msg-self");

        Assert.True(res.IsSuccess);
        Assert.True(fileService.FileExists("chat", "chan-1", "shared_safe.jpg"));
    }

    [Fact]
    public async Task FileService_DeleteAsync_RefusesEmailAccounts_AndCloneFileAsync_EnforcesActorAuthorization()
    {
        var emailAccountsFile = Path.Combine(_testDataPath, "EmailAccounts", "acc-1", "secret.json");
        Directory.CreateDirectory(Path.GetDirectoryName(emailAccountsFile)!);
        await File.WriteAllTextAsync(emailAccountsFile, "{\"password\":\"secret\"}");

        var services = new ServiceCollection();
        var mockFileAuth = new Mock<IFileAuthorizationService>();
        var allowedUser = new Employee { Id = "emp-ok", IsActive = true };
        var deniedUser = new Employee { Id = "emp-no", IsActive = true };

        mockFileAuth.Setup(a => a.CanAccess("projects", "proj-1", allowedUser)).Returns(true);
        mockFileAuth.Setup(a => a.CanUpload("projects", "proj-2", allowedUser)).Returns(true);
        mockFileAuth.Setup(a => a.ResolveDownloadKey("projects", "proj-1", allowedUser, null, null, It.IsAny<string?>()))
            .Returns(FileKeyResolutionResult.NotEncrypted());
        mockFileAuth.Setup(a => a.ResolveUploadKey(It.Is<FileUploadKeyRequest>(r =>
            r.Category == "projects" && r.ContextId == "proj-2" && r.User == allowedUser)))
            .Returns(FileKeyResolutionResult.NotEncrypted());

        services.AddSingleton(mockFileAuth.Object);
        using var sp = services.BuildServiceProvider();

        var fileService = new FileService(
            _config,
            NullLogger<FileService>.Instance,
            _mockCrypto.Object,
            sp.GetRequiredService<IServiceScopeFactory>());

        // EmailAccounts directory must never be deletable via FileService.DeleteAsync
        Assert.False(await fileService.DeleteAsync(emailAccountsFile));
        Assert.True(File.Exists(emailAccountsFile));

        // Seed temp and project files
        string tempUrl;
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("temp-data")))
        {
            tempUrl = await fileService.UploadStreamAsync("temp", "tmp-ctx", ms, "temp.txt", predefinedSafeName: "temp_safe.txt");
        }

        string projectUrl;
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("project-image")))
        {
            projectUrl = await fileService.UploadStreamAsync("projects", "proj-1", ms, "quote.png", predefinedSafeName: "quote_safe.png");
        }

        // Unattributed clone fails closed
        Assert.Null(await fileService.CloneFileAsync(projectUrl, "projects", "proj-2"));

        // Temp clone blocked for non-system callers
        Assert.Null(await fileService.CloneFileAsync(tempUrl, "projects", "proj-2", allowedUser));

        // Unauthorized actor blocked
        Assert.Null(await fileService.CloneFileAsync(projectUrl, "projects", "proj-2", deniedUser));

        // Authorized actor succeeds
        var clonedUrl = await fileService.CloneFileAsync(projectUrl, "projects", "proj-2", allowedUser);
        Assert.NotNull(clonedUrl);
        Assert.True(fileService.TryResolveScopedFileName(clonedUrl, "projects", "proj-2", out _));
    }
}
