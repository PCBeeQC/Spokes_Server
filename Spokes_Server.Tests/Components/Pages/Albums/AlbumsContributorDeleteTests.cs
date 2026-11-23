using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Albums;

public class AlbumsContributorDeleteTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;
    private readonly Mock<IFileService> _fileServiceMock;
    private readonly List<string> _deletedFilePaths = [];

    private readonly Employee _owner;
    private readonly Employee _contributorA;
    private readonly Employee _contributorB;
    private Employee _activeCaller;

    public AlbumsContributorDeleteTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_AlbumContribDel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddMemoryCache();
        Services.AddHttpClient();
        Services.AddDataProtection().UseEphemeralDataProtectionProvider();

        var envMock = new Mock<IWebHostEnvironment>();
        envMock.SetupGet(e => e.WebRootPath).Returns(_testDataDir);
        envMock.SetupGet(e => e.ContentRootPath).Returns(_testDataDir);
        envMock.SetupGet(e => e.EnvironmentName).Returns("Testing");
        Services.AddSingleton(envMock.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataDir,
                ["DatabasePath"] = _testDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["Spokes_DemoMode"] = "false"
            })
            .Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());

        Services.AddSpokesConfiguration(config);
        Services.AddSpokesDatabase();
        Services.AddSpokesCoreServices();
        Services.AddSingleton<SessionService>();

        _fileServiceMock = new Mock<IFileService>();
        _fileServiceMock
            .Setup(f => f.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(p => _deletedFilePaths.Add(p))
            .ReturnsAsync(true);
        Services.Replace(ServiceDescriptor.Singleton(_fileServiceMock.Object));

        _owner = new Employee
        {
            Id = "emp-owner",
            FirstName = "Olivia",
            LastName = "Owner",
            IsActive = true,
            Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use]
        };
        _contributorA = new Employee
        {
            Id = "emp-contrib-a",
            FirstName = "Alice",
            LastName = "Contributor",
            IsActive = true,
            Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use]
        };
        _contributorB = new Employee
        {
            Id = "emp-contrib-b",
            FirstName = "Bob",
            LastName = "Contributor",
            IsActive = true,
            Permissions = [AppPermissions.Albums.View, AppPermissions.Chat.Use]
        };
        _activeCaller = _contributorA;

        var authProviderMock = new Mock<AuthenticationStateProvider>();
        authProviderMock
            .Setup(p => p.GetAuthenticationStateAsync())
            .ReturnsAsync(() =>
            {
                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, _activeCaller.Id),
                    new Claim("EmployeeId", _activeCaller.Id),
                    new Claim("SessionId", $"sess-{_activeCaller.Id}")
                ], authenticationType: "TestAuth");
                return new AuthenticationState(new ClaimsPrincipal(identity));
            });
        Services.AddSingleton<AuthenticationStateProvider>(authProviderMock.Object);

        Services.AddScoped<UserCircuitContext>();
        Services.AddScoped<StartupTimingService>();

        _db = Services.GetRequiredService<Database>();
        _db.Employees.Save(_owner);
        _db.Employees.Save(_contributorA);
        _db.Employees.Save(_contributorB);
        foreach (var emp in new[] { _owner, _contributorA, _contributorB })
        {
            _db.DeviceSessions.Save(new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = $"sess-{emp.Id}",
                EmployeeId = emp.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
        }

        var circuitContext = Services.GetRequiredService<UserCircuitContext>();
        circuitContext.SessionId = $"sess-{_contributorA.Id}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
        }
        base.Dispose(disposing);
    }

    private Album SeedAlbum(string id, List<AlbumMedia> media)
    {
        var album = new Album
        {
            Id = id,
            Title = "Family Trip",
            OwnerId = _owner.Id,
            ContributorUserIds = [_contributorA.Id, _contributorB.Id],
            IsEncrypted = false,
            Media = media
        };
        _db.Albums.Save(album);
        return album;
    }

    [Fact]
    public void Contributor_WithNoOwnUploads_DoesNotSeeSelectOrDeleteControls()
    {
        _activeCaller = _contributorA;
        var album = SeedAlbum("alb-no-own", [
            new AlbumMedia { Id = "m-owner", FileName = "owner.jpg", FilePath = "/spokesapi/files/albums/alb-no-own/owner.jpg", ContentType = "image/jpeg", AddedByUserId = _owner.Id },
            new AlbumMedia { Id = "m-bob", FileName = "bob.jpg", FilePath = "/spokesapi/files/albums/alb-no-own/bob.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorB.Id }
        ]);

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Albums.Albums>(p => p.Add(x => x.Id, album.Id));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".album-media-card").Count));

        Assert.Empty(cut.FindAll("button.media-delete-btn"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Select");
    }

    [Fact]
    public async Task Contributor_CanOnlySelectAndBulkDeleteOwnUploads()
    {
        _activeCaller = _contributorA;
        var album = SeedAlbum("alb-mixed-bulk", [
            new AlbumMedia { Id = "m-owner", FileName = "owner.jpg", FilePath = "/spokesapi/files/albums/alb-mixed-bulk/owner.jpg", ContentType = "image/jpeg", AddedByUserId = _owner.Id },
            new AlbumMedia { Id = "m-alice", FileName = "alice.jpg", FilePath = "/spokesapi/files/albums/alb-mixed-bulk/alice.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorA.Id },
            new AlbumMedia { Id = "m-bob", FileName = "bob.jpg", FilePath = "/spokesapi/files/albums/alb-mixed-bulk/bob.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorB.Id }
        ]);

        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Albums.Albums>(p => p.Add(x => x.Id, album.Id));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".album-media-card").Count));

        // 1. In normal mode, hover delete button only renders on Alice's own photo card (index 1)
        var cards = cut.FindAll(".album-media-card");
        Assert.Empty(cards[0].QuerySelectorAll("button.media-delete-btn"));
        Assert.Single(cards[1].QuerySelectorAll("button.media-delete-btn"));
        Assert.Empty(cards[2].QuerySelectorAll("button.media-delete-btn"));

        // 2. Enter selection mode
        var selectBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Select");
        await cut.InvokeAsync(() => selectBtn.Click());

        cut.WaitForAssertion(() =>
        {
            var deleteSelected = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Delete Selected (0)", StringComparison.Ordinal));
            Assert.NotNull(deleteSelected);
        });

        // Only Alice's own card displays a selection badge; owner's and Bob's cards are marked non-deletable
        cards = cut.FindAll(".album-media-card");
        Assert.Contains("album-media-not-deletable", cards[0].ClassList);
        Assert.Empty(cards[0].QuerySelectorAll(".album-selection-badge"));

        Assert.DoesNotContain("album-media-not-deletable", cards[1].ClassList);
        Assert.Single(cards[1].QuerySelectorAll(".album-selection-badge"));

        Assert.Contains("album-media-not-deletable", cards[2].ClassList);
        Assert.Empty(cards[2].QuerySelectorAll(".album-selection-badge"));

        // 3. Clicking owner's and Bob's cards must NOT select them
        await cut.InvokeAsync(() => cut.FindAll(".album-media-card")[0].Click());
        await cut.InvokeAsync(() => cut.FindAll(".album-media-card")[2].Click());
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Delete Selected (0)", StringComparison.Ordinal) && b.HasAttribute("disabled"));

        // 4. Clicking Alice's own card selects it -> Delete Selected (1)
        await cut.InvokeAsync(() => cut.FindAll(".album-media-card")[1].Click());
        var deleteSelectedBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Delete Selected (1)", StringComparison.Ordinal));
        Assert.False(deleteSelectedBtn.HasAttribute("disabled"));

        // 5. Confirm bulk delete -> removes ONLY Alice's photo and exits selection mode
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Delete Selected (1)", StringComparison.Ordinal)).Click());
        dialogProvider.WaitForAssertion(() =>
            Assert.Contains("Delete Selected Media", dialogProvider.Find(".mud-message-box").TextContent, StringComparison.Ordinal));

        var confirmBtn = dialogProvider.FindAll(".mud-message-box button")
            .Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
        await dialogProvider.InvokeAsync(() => confirmBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".album-media-card").Count));

        var saved = _db.Albums.GetById(album.Id)!;
        Assert.Equal(2, saved.Media.Count);
        Assert.Contains(saved.Media, m => m.Id == "m-owner");
        Assert.Contains(saved.Media, m => m.Id == "m-bob");
        Assert.DoesNotContain(saved.Media, m => m.Id == "m-alice");

        Assert.Single(_deletedFilePaths);
        Assert.Equal("/spokesapi/files/albums/alb-mixed-bulk/alice.jpg", _deletedFilePaths[0]);

        // Select button is now hidden because Alice has no remaining uploaded items
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Select");
    }

    [Fact]
    public async Task Contributor_HoverDeleteOnOwnPhoto_DeletesOnlyOwnPhotoWithoutOpeningLightbox()
    {
        _activeCaller = _contributorA;
        var album = SeedAlbum("alb-single-del", [
            new AlbumMedia { Id = "m-owner", FileName = "owner.jpg", FilePath = "/spokesapi/files/albums/alb-single-del/owner.jpg", ContentType = "image/jpeg", AddedByUserId = _owner.Id },
            new AlbumMedia { Id = "m-alice", FileName = "alice.jpg", FilePath = "/spokesapi/files/albums/alb-single-del/alice.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorA.Id }
        ]);

        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Albums.Albums>(p => p.Add(x => x.Id, album.Id));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".album-media-card").Count));

        var deleteBtns = cut.FindAll("button.media-delete-btn");
        Assert.Single(deleteBtns);

        await cut.InvokeAsync(() => deleteBtns[0].Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".album-media-card")));

        var saved = _db.Albums.GetById(album.Id)!;
        Assert.Single(saved.Media);
        Assert.Equal("m-owner", saved.Media[0].Id);
        Assert.Single(_deletedFilePaths);
        Assert.Equal("/spokesapi/files/albums/alb-single-del/alice.jpg", _deletedFilePaths[0]);
        Assert.Empty(dialogProvider.FindAll(".album-media-dialog-container"));
    }

    [Fact]
    public async Task Owner_CanDeleteAndSelectAllMediaInAlbum()
    {
        _activeCaller = _owner;
        var circuitContext = Services.GetRequiredService<UserCircuitContext>();
        circuitContext.SessionId = $"sess-{_owner.Id}";

        var album = SeedAlbum("alb-owner-all", [
            new AlbumMedia { Id = "m-owner", FileName = "owner.jpg", FilePath = "/spokesapi/files/albums/alb-owner-all/owner.jpg", ContentType = "image/jpeg", AddedByUserId = _owner.Id },
            new AlbumMedia { Id = "m-alice", FileName = "alice.jpg", FilePath = "/spokesapi/files/albums/alb-owner-all/alice.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorA.Id },
            new AlbumMedia { Id = "m-bob", FileName = "bob.jpg", FilePath = "/spokesapi/files/albums/alb-owner-all/bob.jpg", ContentType = "image/jpeg", AddedByUserId = _contributorB.Id }
        ]);

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Albums.Albums>(p => p.Add(x => x.Id, album.Id));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".album-media-card").Count));

        // Owner sees hover delete button on every media tile
        Assert.Equal(3, cut.FindAll("button.media-delete-btn").Count);

        var selectBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Select");
        await cut.InvokeAsync(() => selectBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".album-selection-badge").Count));
        Assert.Empty(cut.FindAll(".album-media-not-deletable"));
    }
}
