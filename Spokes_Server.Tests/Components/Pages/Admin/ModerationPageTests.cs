using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Pages.Admin;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Components.Pages.Admin;

/// <summary>
/// Interactive bUnit tests verifying Moderation Dashboard (/admin/moderation) search bar filtering
/// by reporter display name, reported user display name, reason, snapshot content, and empty-filter state.
/// </summary>
public class ModerationPageTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;

    public ModerationPageTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ModerationPage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddLogging();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        Services.AddSingleton<IConfiguration>(mockConfig.Object);

        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();
        Services.AddSingleton<ChatStateService>();
        Services.AddSingleton<PresenceStateService>(sp =>
            new PresenceStateService(sp.GetRequiredService<ChatStateService>()));

        var mockChatService = new Mock<IChatService>();
        Services.AddSingleton<IChatService>(mockChatService.Object);
        Services.AddSingleton<ModerationReportService>();
        Services.AddSingleton<IModerationReportService>(sp =>
            sp.GetRequiredService<ModerationReportService>());

        const string moderatorId = "emp-moderator";
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, moderatorId),
                new Claim("EmployeeId", moderatorId)
            ],
            authenticationType: "TestAuth");
        var authState = new AuthenticationState(new ClaimsPrincipal(identity));
        var mockAuthProvider = new Mock<AuthenticationStateProvider>();
        mockAuthProvider
            .Setup(p => p.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        Services.AddSingleton<AuthenticationStateProvider>(mockAuthProvider.Object);
        Services.AddScoped<UserService>();

        _db = Services.GetRequiredService<Database>();
        _db.Employees.Save(new Employee
        {
            Id = moderatorId,
            FirstName = "Morgan",
            LastName = "Mod",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Moderator]
        });
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

    private void SeedReports()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", LastName = "Wonder", IsActive = true };
        var victor = new Employee { Id = "emp-victor", FirstName = "Victor", LastName = "Target", IsActive = true };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Offender", IsActive = true };
        var charlie = new Employee { Id = "emp-charlie", FirstName = "Charlie", LastName = "Sender", IsActive = true };

        _db.Employees.Save(alice);
        _db.Employees.Save(victor);
        _db.Employees.Save(bob);
        _db.Employees.Save(charlie);

        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-1",
            MessageId = "msg-1",
            ReporterId = alice.Id,
            ReportedUserId = victor.Id,
            Reason = "Spam or Scams",
            SnapshotContent = "First reported payload",
            Status = ReportedMessageStatus.Open,
            ReportedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc)
        });

        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-2",
            MessageId = "msg-2",
            ReporterId = charlie.Id,
            ReportedUserId = bob.Id,
            Reason = "Harassment or Bullying",
            SnapshotContent = "Second reported payload",
            Status = ReportedMessageStatus.Open,
            ReportedAt = new DateTime(2026, 10, 30, 12, 5, 0, DateTimeKind.Utc)
        });
    }

    [Fact]
    public async Task SearchBar_FiltersCardsLiveByReporter_ReportedUser_Reason_AndShowsEmptySearchFeedback()
    {
        SeedReports();

        Render<MudPopoverProvider>();
        var cut = Render<Moderation>();

        // Initially both open report cards are displayed
        Assert.Equal(2, cut.FindAll(".moderation-card").Count);

        var searchField = cut.FindComponent<MudTextField<string>>();

        // 1. Search by reporter name ("Alice") -> matches only rep-1 (Alice reporting Victor)
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Alice"));
        var aliceCards = cut.FindAll(".moderation-card");
        Assert.Single(aliceCards);
        Assert.Contains("First reported payload", aliceCards[0].TextContent);
        Assert.Contains("Alice Wonder", aliceCards[0].TextContent);

        // 2. Search by reported user name ("Victor") -> matches only rep-1
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Victor"));
        var victorCards = cut.FindAll(".moderation-card");
        Assert.Single(victorCards);
        Assert.Contains("Victor Target", victorCards[0].TextContent);

        // 3. Search by reported user name ("Bob") -> matches only rep-2
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Bob"));
        var bobCards = cut.FindAll(".moderation-card");
        Assert.Single(bobCards);
        Assert.Contains("Second reported payload", bobCards[0].TextContent);
        Assert.Contains("Bob Offender", bobCards[0].TextContent);

        // 4. Search by reason ("Harassment") -> matches only rep-2
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Harassment"));
        Assert.Single(cut.FindAll(".moderation-card"));

        // 5. Non-matching search shows empty-filter notice (NOT "All Clear!")
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("NobodyWithThisName"));
        Assert.Empty(cut.FindAll(".moderation-card"));
        Assert.Contains("No matching reports", cut.Markup);
        Assert.DoesNotContain("All Clear!", cut.Markup);

        // 6. Clearing search restores all cards
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync(""));
        Assert.Equal(2, cut.FindAll(".moderation-card").Count);
    }

    [Fact]
    public async Task SearchBar_MatchesSystemDeletedUser_AndHidesSuspendBanControlsForSystemAccount()
    {
        SeedReports();

        var deletedUser = new Employee
        {
            Id = "system-deleted-user",
            FirstName = "Deleted",
            LastName = "User",
            IsActive = false,
            IsSystem = true
        };
        _db.Employees.Save(deletedUser);

        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-deleted",
            MessageId = "msg-deleted",
            ReporterId = "emp-alice",
            ReportedUserId = deletedUser.Id,
            Reason = "Other",
            SnapshotContent = "Legacy deleted author message",
            Status = ReportedMessageStatus.Open,
            ReportedAt = new DateTime(2026, 10, 30, 12, 10, 0, DateTimeKind.Utc)
        });

        Render<MudPopoverProvider>();
        var cut = Render<Moderation>();

        var searchField = cut.FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Deleted User"));

        var cards = cut.FindAll(".moderation-card");
        Assert.Single(cards);
        Assert.Contains("Deleted User", cards[0].TextContent);
        Assert.DoesNotContain("Suspend User", cards[0].TextContent);
        Assert.DoesNotContain("Ban User", cards[0].TextContent);
    }
}
