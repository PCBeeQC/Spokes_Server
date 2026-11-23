using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Hubs;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Security;

public class PrivacyAvatarDirectoryAndTeamPolishTests : TestDataTestBase
{
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly ChatChannelRepository _channels;
    private readonly AlbumRepository _albums;
    private readonly UserPrivacyService _privacyService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly string _webRootPath;

    public PrivacyAvatarDirectoryAndTeamPolishTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _serverConfigs = new ServerConfigRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _projects = new ProjectRepository(_persistence, _config);
        _openIdAccounts = new OpenIdAccountRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _albums = new AlbumRepository(_persistence, _config);
        _privacyService = new UserPrivacyService(_companyProfiles, _employees, _teams);

        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _dataProtection = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();

        _webRootPath = Path.Combine(Path.GetTempPath(), "Spokes_Test_WS41_WebRoot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRootPath);
    }

    public override void Dispose()
    {
        _persistence.FlushAll();
        _persistence.Dispose();
        if (Directory.Exists(_webRootPath))
        {
            try { Directory.Delete(_webRootPath, true); } catch { }
        }
        base.Dispose();
    }

    private UserService CreateUserService(Employee? caller)
    {
        var identity = caller == null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(
            [
                new Claim("EmployeeId", caller.Id),
                new Claim(ClaimTypes.NameIdentifier, caller.Id),
                new Claim("sub", caller.Id)
            ], "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        return new UserService(mockAuth.Object, _employees, _openIdAccounts, _companyProfiles, _teams, sessionService: null);
    }

    private MediaController CreateMediaController(Employee? caller, ClaimsPrincipal? explicitPrincipal = null)
    {
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns(_webRootPath);

        var avatarGen = new AvatarGeneratorService(mockEnv.Object);
        var userService = CreateUserService(caller);
        var fileService = new FileService(_config, NullLogger<FileService>.Instance, new CryptoService());
        var fileAuth = new FileAuthorizationService([new AvatarFileAccessProvider(_employees, _privacyService)]);

        var controller = new MediaController(
            _companyProfiles,
            _employees,
            mockEnv.Object,
            _dataProtection,
            avatarGen,
            userService,
            fileService,
            fileAuth);

        var principal = explicitPrincipal ?? (caller == null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("EmployeeId", caller.Id),
                new Claim(ClaimTypes.NameIdentifier, caller.Id),
                new Claim("sub", caller.Id)
            ], "TestAuth")));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public void AvatarFileAccessProvider_EnforcesUserPrivacy_ForStealthAdminAndIsolatedPeers()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.ContactsOnly,
            AdminVisibility = AdminVisibilityMode.None
        };
        _companyProfiles.Save(profile);

        var caller = new Employee { Id = "emp-caller", IsActive = true, FirstName = "Caller", LastName = "User" };
        var stealthAdmin = new Employee { Id = "emp-stealth", IsActive = true, IsAdmin = true, FirstName = "Stealth", LastName = "Admin" };
        var isolatedPeer = new Employee { Id = "emp-isolated", IsActive = true, FirstName = "Isolated", LastName = "Peer" };
        var contactPeer = new Employee { Id = "emp-contact", IsActive = true, FirstName = "Contact", LastName = "Peer" };
        caller.ContactUserIds.Add(contactPeer.Id);

        _employees.Save(caller);
        _employees.Save(stealthAdmin);
        _employees.Save(isolatedPeer);
        _employees.Save(contactPeer);

        var voiceState = new VoiceStateService();
        var provider = new AvatarFileAccessProvider(_employees, _privacyService, voiceState);

        Assert.True(provider.CanAccess(caller, caller.Id));
        Assert.True(provider.CanAccess(caller, contactPeer.Id));
        Assert.False(provider.CanAccess(caller, stealthAdmin.Id));
        Assert.False(provider.CanAccess(caller, isolatedPeer.Id));
        Assert.False(provider.CanAccess(caller, "missing-emp"));
        Assert.True(provider.CanAccess(stealthAdmin, isolatedPeer.Id));

        // When stealthAdmin or isolatedPeer is actively connected to a voice channel, co-participants can load their avatar
        voiceState.NotifyVoiceMemberJoined("voice-ch-1", stealthAdmin.Id);
        Assert.True(provider.CanAccess(caller, stealthAdmin.Id));
        voiceState.NotifyVoiceMemberLeft("voice-ch-1", stealthAdmin.Id);
        Assert.False(provider.CanAccess(caller, stealthAdmin.Id));
    }

    [Fact]
    public void MediaController_GetAvatar_DeniesUndiscoverableUserWith403_AllowsValidPushTokenWithStaleBannedCookie()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.ContactsOnly,
            AdminVisibility = AdminVisibilityMode.None
        };
        _companyProfiles.Save(profile);

        var caller = new Employee { Id = "caller-1", IsActive = true, FirstName = "Caller", LastName = "One" };
        var target = new Employee { Id = "stealth-target", IsActive = true, IsAdmin = true, ProfileColor = "#2196F3", FirstName = "Hidden", LastName = "Admin" };
        var bannedUser = new Employee { Id = "banned-cookie-user", IsActive = true, IsBanned = true, FirstName = "Banned", LastName = "User" };
        _employees.Save(caller);
        _employees.Save(target);
        _employees.Save(bannedUser);

        var deniedController = CreateMediaController(caller);
        var deniedResult = deniedController.GetAvatar(target.Id, t: null);
        var forbidden = Assert.IsType<ObjectResult>(deniedResult);
        Assert.Equal(403, forbidden.StatusCode);

        var empDir = Path.Combine(_testDataPath, "Employees", target.Id);
        Directory.CreateDirectory(empDir);
        var avatarPath = Path.Combine(empDir, "avatar.png");
        File.WriteAllBytes(avatarPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]);
        target.AvatarFile = "avatar.png";
        _employees.Save(target);

        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var pushToken = protector.Protect($"{target.Id}|{DateTime.UtcNow.AddMinutes(10).Ticks.ToString(CultureInfo.InvariantCulture)}");

        var bannedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", bannedUser.Id),
            new Claim(ClaimTypes.NameIdentifier, bannedUser.Id)
        ], "TestAuth"));
        var pushTokenController = CreateMediaController(bannedUser, bannedPrincipal);

        var allowedResult = pushTokenController.GetAvatar(target.Id, t: pushToken);
        var physical = Assert.IsType<PhysicalFileResult>(allowedResult);
        Assert.Equal(Path.GetFullPath(avatarPath), physical.FileName);
    }

    [Fact]
    public async Task SlideshowHub_PairWithTv_NeverExposesAddedByUserNameInStagePayload()
    {
        var uploader = new Employee
        {
            Id = "stealth-uploader",
            FirstName = "Secret",
            LastName = "Uploader",
            IsActive = true,
            IsAdmin = true,
            Permissions = [AppPermissions.Albums.View]
        };
        _employees.Save(uploader);

        var album = new Album
        {
            Id = "album-41",
            Title = "Shared Album",
            OwnerId = uploader.Id,
            IsEncrypted = false,
            Media =
            [
                new AlbumMedia
                {
                    Id = "m1",
                    FileName = "photo.jpg",
                    FilePath = "/spokesapi/files/albums/album-41/photo.jpg",
                    ContentType = "image/jpeg",
                    AddedByUserId = uploader.Id
                }
            ]
        };
        _albums.Save(album);

        var mockDb = new Mock<Database>();
        mockDb.Setup(d => d.Albums).Returns(_albums);
        mockDb.Setup(d => d.Employees).Returns(_employees);

        var sessionService = new SlideshowSessionService();
        var tvSession = sessionService.CreateSession("tv-conn-41");

        var escrow = new Mock<ServerEscrowService>(_companyProfiles, _serverConfigs, new CryptoService(), NullLogger<ServerEscrowService>.Instance, _config);
        var albumService = new AlbumService(_albums, _channels, new CryptoService(), escrow.Object, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees));
        var fileTokenService = new FileTokenService(_dataProtection, new MemoryCache(new MemoryCacheOptions()));
        var keystore = new ScopedKeystoreService(_dataProtection);
        var userService = CreateUserService(uploader);

        var mockClients = new Mock<IHubCallerClients>();
        var mockSingleClient = new Mock<ISingleClientProxy>();
        var mockGroupProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(mockSingleClient.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroupProxy.Object);

        SlideshowStagePayload? capturedPayload = null;
        mockSingleClient
            .Setup(p => p.SendCoreAsync("SessionStarted", It.IsAny<object[]>(), default))
            .Callback<string, object[], System.Threading.CancellationToken>((_, args, _) =>
            {
                capturedPayload = args[0] as SlideshowStagePayload;
            })
            .Returns(Task.CompletedTask);

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("host-conn-41");
        mockContext.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", uploader.Id),
            new Claim("sub", uploader.Id)
        ], "TestAuth")));

        var hub = new SlideshowHub(
            sessionService,
            mockDb.Object,
            albumService,
            userService,
            keystore,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SlideshowHub>.Instance)
        {
            Context = mockContext.Object,
            Clients = mockClients.Object,
            Groups = new Mock<IGroupManager>().Object
        };

        var result = await hub.PairWithTv(tvSession.PairingPin, album.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedPayload);
        Assert.Single(capturedPayload!.Items);
        Assert.Equal(string.Empty, capturedPayload.Items[0].AddedByUserName);
    }

    [Fact]
    public void ProjectAndEmployeeTargetHelpers_EnforceProjectVisibilityAndAdminPreservation()
    {
        var restrictedProject = new Project
        {
            Id = "proj-secret",
            ProjectNumber = "PRJ-SECRET",
            Name = "Confidential Acquisition",
            IsActive = true,
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["admin-1"]
        };
        var openProject = new Project
        {
            Id = "proj-open",
            ProjectNumber = "PRJ-OPEN",
            Name = "Public Renovation",
            IsActive = true,
            AccessPolicy = ProjectAccessPolicy.Public
        };
        _projects.Save(restrictedProject);
        _projects.Save(openProject);

        var projectViewer = new Employee
        {
            Id = "emp-reg",
            IsActive = true,
            Permissions = [AppPermissions.Calendar.View, AppPermissions.Projects.View]
        };
        var calendarOnlyUser = new Employee
        {
            Id = "emp-cal-only",
            IsActive = true,
            Permissions = [AppPermissions.Calendar.View]
        };
        var hrManager = new Employee
        {
            Id = "emp-hr",
            IsActive = true,
            Permissions = [AppPermissions.Hr.ManageEmployees]
        };
        var targetWorker = new Employee { Id = "emp-worker", IsActive = true };
        var adminUser = new Employee { Id = "admin-1", IsActive = true, IsAdmin = true };
        _employees.Save(projectViewer);
        _employees.Save(calendarOnlyUser);
        _employees.Save(hrManager);
        _employees.Save(targetWorker);
        _employees.Save(adminUser);

        var userService = CreateUserService(projectViewer);

        Assert.False(userService.CanViewProject(projectViewer, restrictedProject));
        Assert.True(userService.CanViewProject(projectViewer, openProject));
        Assert.False(userService.CanViewProject(calendarOnlyUser, openProject));
        Assert.True(userService.CanViewProject(adminUser, restrictedProject));

        Assert.False(UserService.CanManageEmployeeTarget(hrManager, adminUser));
        Assert.True(UserService.CanManageEmployeeTarget(hrManager, targetWorker));
        Assert.True(UserService.CanManageEmployeeTarget(adminUser, adminUser));
    }

    [Fact]
    public void AddressBookPermission_ReadOnlyAllowedForSuspendedUser_DeniedForUserWithoutPermission()
    {
        var permitted = new Employee
        {
            Id = "ab-view",
            IsActive = true,
            IsSuspended = true,
            Permissions = [AppPermissions.AddressBook.View]
        };
        var unpermitted = new Employee
        {
            Id = "ab-none",
            IsActive = true,
            Permissions = [AppPermissions.Email.Use]
        };

        Assert.True(permitted.HasEffectivePermission(AppPermissions.AddressBook.View, allowSuspended: true));
        Assert.False(permitted.HasEffectivePermission(AppPermissions.AddressBook.View, allowSuspended: false));
        Assert.False(unpermitted.HasEffectivePermission(AppPermissions.AddressBook.View, allowSuspended: true));
    }
}
