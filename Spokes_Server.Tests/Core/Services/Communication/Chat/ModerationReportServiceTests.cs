using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ModerationReportServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly ReportedMessageRepository _reportedMessages;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<INotificationRoutingService> _mockNotificationRouting;
    private readonly ChatStateService _chatState;
    private readonly FakeTimeProvider _clock;
    private readonly ModerationReportService _service;

    public ModerationReportServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ModReports_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "DataPath", _testDataDir }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" } })
            .Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _reportedMessages = new ReportedMessageRepository(_persistence, config);
        _channels = new ChatChannelRepository(_persistence, config);
        _channels.Save(new ChatChannel { Id = "chan-general", Name = "General", ChannelType = ChatChannelType.General });
        _employees = new EmployeeRepository(_persistence, config);
        _chatAuth = new ChatAuthorizationService(
            _channels,
            new ProjectRepository(_persistence, config),
            new TeamRepository(_persistence, config),
            _employees);
        _mockChatService = new Mock<IChatService>();
        _mockNotificationRouting = new Mock<INotificationRoutingService>();
        _chatState = new ChatStateService();
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ReportedMessages).Returns(_reportedMessages);
        commContext.Setup(c => c.ChatChannels).Returns(_channels);

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);

        _service = new ModerationReportService(
            commContext.Object,
            hrContext.Object,
            _mockChatService.Object,
            _chatState,
            _mockNotificationRouting.Object,
            _clock,
            chatAuth: _chatAuth);
    }

    public void Dispose()
    {
        _persistence.FlushAll();
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    private (Employee Moderator, Employee Reporter, Employee Offender) SeedUsers()
    {
        var moderator = new Employee
        {
            Id = "emp-mod",
            FirstName = "Morgan",
            LastName = "Mod",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Moderator, AppPermissions.Chat.Use]
        };
        var reporter = new Employee
        {
            Id = "emp-alice",
            FirstName = "Alice",
            LastName = "Reporter",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        var offender = new Employee
        {
            Id = "emp-victor",
            FirstName = "Victor",
            LastName = "Offender",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };

        _employees.Save(moderator);
        _employees.Save(reporter);
        _employees.Save(offender);

        return (moderator, reporter, offender);
    }

    [Fact]
    public async Task SubmitReportAsync_PersistsOpenReport_RoutesNotification_AndFiresAlert()
    {
        var (_, reporter, offender) = SeedUsers();
        ReportedMessage? alertedReport = null;
        _chatState.ModerationAlertReceived += r => alertedReport = r;

        var draft = new ReportedMessage
        {
            MessageId = "msg-100",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Spam or Scams",
            SnapshotContent = "Buy free crypto!"
        };

        var result = await _service.SubmitReportAsync(draft, reporter.Id);
        _persistence.FlushAll();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(reporter.Id, result.Value!.ReporterId);
        Assert.Equal(ReportedMessageStatus.Open, result.Value.Status);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, result.Value.ReportedAt);
        Assert.Same(result.Value, alertedReport);

        _mockNotificationRouting.Verify(n => n.RouteModerationNotificationAsync(result.Value), Times.Once);

        var persistedPath = Path.Combine(_testDataDir, "Communication", "ReportedMessages", $"{result.Value.Id}.json");
        Assert.True(File.Exists(persistedPath));
    }

    [Fact]
    public async Task SubmitReportAsync_SuspendedOrBannedReporter_IsRejected()
    {
        var (_, reporter, offender) = SeedUsers();
        reporter.IsSuspended = true;
        _employees.Save(reporter);

        var result = await _service.SubmitReportAsync(new ReportedMessage { MessageId = "m1", ReportedUserId = offender.Id }, reporter.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("ACCESS_DENIED", result.Code);
        Assert.Empty(_reportedMessages.GetAll());
    }

    [Fact]
    public async Task DismissAndResolve_MovesReportsFromOpenToHistory_RestoringZeroOpenQueue()
    {
        var (moderator, reporter, offender) = SeedUsers();
        _mockChatService
            .Setup(c => c.AdminDeleteMessageAsync("msg-2", moderator.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult.Success());

        var r1 = (await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-1",
            MessageId = "msg-1",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Spam or Scams",
            SnapshotContent = "Spam link"
        }, reporter.Id)).Value!;

        _clock.Advance(TimeSpan.FromMinutes(1));

        var r2 = (await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-2",
            MessageId = "msg-2",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Harassment or Bullying",
            SnapshotContent = "Rude text",
            Attachments = [new ChatAttachment { Id = "att-1", FileName = "bad.jpg", FilePath = "/media/bad.jpg" }]
        }, reporter.Id)).Value!;

        Assert.Equal(2, _service.GetCounts(moderator.Id).Open);
        Assert.Equal(0, _service.GetCounts(moderator.Id).History);
        Assert.Equal(2, _service.GetReportItems(ModerationQueueTab.Open, viewerEmployeeId: moderator.Id).Count);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var dismissResult = _service.DismissReport(r1.Id, moderator.Id);
        Assert.True(dismissResult.IsSuccess);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var resolveResult = await _service.ResolveAndDeleteMessageAsync(r2.Id, moderator.Id);
        Assert.True(resolveResult.IsSuccess);
        Assert.Equal(1, resolveResult.Value);

        // Open queue is now completely empty -> Moderation Dashboard displays 'All Clear!'
        var counts = _service.GetCounts(moderator.Id);
        Assert.Equal(0, counts.Open);
        Assert.Equal(1, counts.Resolved);
        Assert.Equal(1, counts.Dismissed);
        Assert.Equal(2, counts.History);
        Assert.Empty(_service.GetReportItems(ModerationQueueTab.Open, viewerEmployeeId: moderator.Id));

        // History contains both items ordered by ResolvedAt descending (rep-2 first, then rep-1)
        var allHistory = _service.GetReportItems(ModerationQueueTab.History, "all", viewerEmployeeId: moderator.Id).Select(i => i.Report).ToList();
        Assert.Equal(2, allHistory.Count);
        Assert.Equal("rep-2", allHistory[0].Id);
        Assert.Equal(ReportedMessageStatus.Resolved, allHistory[0].Status);
        Assert.Equal(ReportedMessageResolution.DeletedMessage, allHistory[0].ResolutionAction);
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, allHistory[0].SnapshotContent);
        Assert.Empty(allHistory[0].Attachments);
        Assert.NotNull(allHistory[0].ResolvedAt);

        Assert.Equal("rep-1", allHistory[1].Id);
        Assert.Equal(ReportedMessageStatus.Dismissed, allHistory[1].Status);
        Assert.Equal("Morgan Mod", allHistory[1].ResolvedBy);
        Assert.NotNull(allHistory[1].ResolvedAt);

        // Sub-status filtering inside History
        Assert.Single(_service.GetReportItems(ModerationQueueTab.History, ReportedMessageStatus.Resolved, viewerEmployeeId: moderator.Id));
        Assert.Single(_service.GetReportItems(ModerationQueueTab.History, ReportedMessageStatus.Dismissed, viewerEmployeeId: moderator.Id));
    }

    [Fact]
    public async Task GetReportItems_SearchMatchesReporter_ReportedUser_Reason_Snapshot_AndModerator()
    {
        var (moderator, reporter, offender) = SeedUsers();

        await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-search",
            MessageId = "msg-search",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Harassment or Bullying",
            SnapshotContent = "UniquePayloadToken42"
        }, reporter.Id);

        // Match by reporter name ("Alice")
        Assert.Single(_service.GetReportItems(ModerationQueueTab.Open, searchQuery: "Alice", viewerEmployeeId: moderator.Id));
        // Match by reported user name ("Victor")
        Assert.Single(_service.GetReportItems(ModerationQueueTab.Open, searchQuery: "victor", viewerEmployeeId: moderator.Id));
        // Match by reason
        Assert.Single(_service.GetReportItems(ModerationQueueTab.Open, searchQuery: "Bullying", viewerEmployeeId: moderator.Id));
        // Match by snapshot content
        Assert.Single(_service.GetReportItems(ModerationQueueTab.Open, searchQuery: "UniquePayloadToken42", viewerEmployeeId: moderator.Id));
        // Non-matching query returns empty list
        Assert.Empty(_service.GetReportItems(ModerationQueueTab.Open, searchQuery: "NonExistentQuery999", viewerEmployeeId: moderator.Id));

        // Dismiss and search history by moderator name ("Morgan")
        _service.DismissReport("rep-search", moderator.Id);
        Assert.Single(_service.GetReportItems(ModerationQueueTab.History, searchQuery: "Morgan", viewerEmployeeId: moderator.Id));
    }

    [Fact]
    public async Task ResolveAndDeleteMessageAsync_CascadesAcrossDuplicateReportsForSameMessageId()
    {
        var (moderator, reporter, offender) = SeedUsers();
        var secondReporter = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Second", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(secondReporter);

        _mockChatService
            .Setup(c => c.AdminDeleteMessageAsync("msg-shared", moderator.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult.Success());

        await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-dup-1",
            MessageId = "msg-shared",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Hate Speech",
            SnapshotContent = "Offending content",
            Attachments = [new ChatAttachment { Id = "a1", FilePath = "/media/1.png" }]
        }, reporter.Id);

        await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-dup-2",
            MessageId = "msg-shared",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Harassment or Bullying",
            SnapshotContent = "Offending content",
            Attachments = [new ChatAttachment { Id = "a1", FilePath = "/media/1.png" }]
        }, secondReporter.Id);

        var result = await _service.ResolveAndDeleteMessageAsync("rep-dup-1", moderator.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(0, _service.GetCounts(moderator.Id).Open);
        Assert.Equal(2, _service.GetCounts(moderator.Id).Resolved);

        var dup2 = _reportedMessages.GetById("rep-dup-2")!;
        Assert.Equal(ReportedMessageStatus.Resolved, dup2.Status);
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, dup2.SnapshotContent);
        Assert.Empty(dup2.Attachments);
    }

    [Fact]
    public async Task ResolveAndDeleteMessageAsync_WhenAuthorAlreadyDeletedMessage_StillResolvesReportGracefully()
    {
        var (moderator, reporter, offender) = SeedUsers();

        _mockChatService
            .Setup(c => c.AdminDeleteMessageAsync("msg-already-gone", moderator.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult.Failure("Message 'msg-already-gone' not found.", "NOT_FOUND"));

        await _service.SubmitReportAsync(new ReportedMessage
        {
            Id = "rep-gone",
            MessageId = "msg-already-gone",
            ChannelId = "chan-general",
            ReportedUserId = offender.Id,
            Reason = "Inappropriate Content",
            SnapshotContent = "Deleted by user before mod arrived"
        }, reporter.Id);

        var result = await _service.ResolveAndDeleteMessageAsync("rep-gone", moderator.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _service.GetCounts(moderator.Id).Open);
        var updated = _reportedMessages.GetById("rep-gone")!;
        Assert.Equal(ReportedMessageStatus.Resolved, updated.Status);
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, updated.SnapshotContent);
    }

    [Fact]
    public async Task DeleteHistoryReport_And_ClearHistory_RemoveResolvedItemsWhilePreservingOpenReports()
    {
        var (moderator, reporter, offender) = SeedUsers();

        await _service.SubmitReportAsync(new ReportedMessage { Id = "rep-open", MessageId = "m1", ChannelId = "chan-general", ReportedUserId = offender.Id, Reason = "Spam" }, reporter.Id);
        await _service.SubmitReportAsync(new ReportedMessage { Id = "rep-hist-1", MessageId = "m2", ChannelId = "chan-general", ReportedUserId = offender.Id, Reason = "Spam" }, reporter.Id);
        await _service.SubmitReportAsync(new ReportedMessage { Id = "rep-hist-2", MessageId = "m3", ChannelId = "chan-general", ReportedUserId = offender.Id, Reason = "Spam" }, reporter.Id);

        _service.DismissReport("rep-hist-1", moderator.Id);
        _service.DismissReport("rep-hist-2", moderator.Id);

        // Open reports cannot be deleted via DeleteHistoryReport
        var rejectOpenDelete = _service.DeleteHistoryReport("rep-open", moderator.Id);
        Assert.False(rejectOpenDelete.IsSuccess);
        Assert.Equal("INVALID_STATE", rejectOpenDelete.Code);

        // Single history item removal works
        var singleDelete = _service.DeleteHistoryReport("rep-hist-1", moderator.Id);
        Assert.True(singleDelete.IsSuccess);
        Assert.Null(_reportedMessages.GetById("rep-hist-1"));

        // ClearHistory deletes remaining history items while leaving rep-open untouched
        var clearResult = _service.ClearHistory(moderator.Id);
        _persistence.FlushAll();

        Assert.True(clearResult.IsSuccess);
        Assert.Equal(1, clearResult.Value);
        Assert.Null(_reportedMessages.GetById("rep-hist-2"));
        Assert.NotNull(_reportedMessages.GetById("rep-open"));
        Assert.Equal(1, _service.GetCounts(moderator.Id).Open);
        Assert.Equal(0, _service.GetCounts(moderator.Id).History);
    }

    [Fact]
    public async Task NonModerator_CannotDismiss_Resolve_OrClearReports()
    {
        var (_, reporter, offender) = SeedUsers();
        await _service.SubmitReportAsync(new ReportedMessage { Id = "rep-sec", MessageId = "m-sec", ChannelId = "chan-general", ReportedUserId = offender.Id }, reporter.Id);

        Assert.False(_service.DismissReport("rep-sec", reporter.Id).IsSuccess);
        Assert.False((await _service.ResolveAndDeleteMessageAsync("rep-sec", reporter.Id)).IsSuccess);
        Assert.False(_service.ClearHistory(reporter.Id).IsSuccess);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset initial) => _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
