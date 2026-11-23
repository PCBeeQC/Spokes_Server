using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.HR.Calendar;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public sealed class CalendarNotificationQueueAndRelaySecurityTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly IConfiguration _config;
    private readonly ServiceProvider _baseProvider;
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly NotificationQueueService _queue;
    private readonly CalendarReminderStateService _reminderState;
    private readonly Mock<IWebPushService> _mockWebPush;

    public CalendarNotificationQueueAndRelaySecurityTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"Spokes_WS37_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataPath);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_config);
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _baseProvider = services.BuildServiceProvider();
        _db = _baseProvider.GetRequiredService<Database>();
        _persistence = _baseProvider.GetRequiredService<DiskPersistenceService>();

        var profile = _db.CoreContext.CompanyProfile.Get();
        profile.CompanyName = "Spokes Security Test Org";
        _db.CoreContext.CompanyProfile.Save(profile);

        _queue = new NotificationQueueService(
            NullLogger<NotificationQueueService>.Instance,
            _persistence,
            _config,
            _db.CoreContext.CompanyProfile,
            _baseProvider.GetRequiredService<EncryptionService>());

        _reminderState = new CalendarReminderStateService();
        _mockWebPush = new Mock<IWebPushService>();
        _mockWebPush.SetupGet(w => w.IsConfigured).Returns(true);
        _mockWebPush.Setup(w => w.SendNotificationDirectAsync(It.IsAny<PushNotificationRequest>()))
            .Returns(Task.CompletedTask);
        _mockWebPush.Setup(w => w.SendNotificationDirectAsync(It.IsAny<PushNotificationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _baseProvider.Dispose();
        try
        {
            if (Directory.Exists(_testDataPath))
            {
                Directory.Delete(_testDataPath, true);
            }
        }
        catch { }
    }

    private Employee SaveEmployee(
        string id,
        bool isAdmin = false,
        bool isSuspended = false,
        bool isActive = true,
        bool isBanned = false,
        string? teamId = null,
        params string[] permissions)
    {
        var employee = new Employee
        {
            Id = id,
            FirstName = "User",
            LastName = id,
            Email = $"{id}@spokes.test",
            IsAdmin = isAdmin,
            IsSuspended = isSuspended,
            IsActive = isActive,
            IsBanned = isBanned,
            TeamId = teamId,
            Permissions = [.. permissions]
        };
        _db.Employees.Save(employee);
        return employee;
    }

    private CalendarService CreateCalendarService() =>
        new(_db.CalendarEvents, _db.Employees, _db.CoreContext.CompanyProfile, null, _db.Teams, _queue);

    private static (string PublicKeyBase64, RSA PrivateKey) CreateRsaPair(int bits = 2048)
    {
        var rsa = RSA.Create(bits);
        var pubBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        return (pubBase64, rsa);
    }

    private static string DecryptRelayInnerPayload(RelayPushPayload outer, RSA privateKey)
    {
        Assert.True(outer.IsEncrypted);
        Assert.False(string.IsNullOrEmpty(outer.EncryptedKey));
        Assert.False(string.IsNullOrEmpty(outer.Nonce));
        Assert.False(string.IsNullOrEmpty(outer.Ciphertext));
        Assert.False(string.IsNullOrEmpty(outer.Tag));

        var aesKey = privateKey.Decrypt(Convert.FromBase64String(outer.EncryptedKey!), RSAEncryptionPadding.OaepSHA1);
        var nonce = Convert.FromBase64String(outer.Nonce!);
        var ciphertext = Convert.FromBase64String(outer.Ciphertext!);
        var tag = Convert.FromBase64String(outer.Tag!);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(aesKey, tagSizeInBytes: 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private PushController CreatePushController(
        string? authenticatedEmployeeId,
        bool nativePushAllowed = true,
        Mock<IWebPushService>? webPushOverride = null)
    {
        var mockAuthState = new Mock<AuthenticationStateProvider>();
        var identity = authenticatedEmployeeId != null
            ? new ClaimsIdentity([new Claim("EmployeeId", authenticatedEmployeeId)], "TestAuth")
            : new ClaimsIdentity();
        var principal = new ClaimsPrincipal(identity);
        mockAuthState.Setup(s => s.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));

        var userService = new UserService(mockAuthState.Object, _db.Employees, _db.CoreContext.OpenIdAccounts);
        var mockLicense = new Mock<IPushLicenseEligibilityService>();
        mockLicense.Setup(l => l.IsNativePushAllowed()).Returns(nativePushAllowed);
        mockLicense.Setup(l => l.GetNativePushStatus()).Returns(new NativePushAvailability(nativePushAllowed, null, null));

        var controller = new PushController(
            _db.DeviceSessions,
            _db.Employees,
            (webPushOverride ?? _mockWebPush).Object,
            userService,
            mockLicense.Object,
            NullLogger<PushController>.Instance,
            _baseProvider.GetRequiredService<IDataProtectionProvider>(),
            Mock.Of<ISystemLogService>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private NotificationQueueBackgroundService CreateQueueBackgroundService(
        Mock<IChatAuthorizationService>? mockChatAuth = null,
        Mock<IEmailInboxService>? mockEmailInbox = null,
        Mock<IWebPushService>? webPushOverride = null,
        Mock<IPushDecisionService>? pushDecisionOverride = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db.HrContext);
        services.AddSingleton(_db.Employees);
        services.AddSingleton(_db.CalendarEvents);
        services.AddSingleton(_db.Teams);
        services.AddSingleton(_db.ChatChannels);
        services.AddSingleton(_db.ChatMessages);
        services.AddSingleton(_db.ChatReadStates);
        services.AddSingleton(_db.EmailMessages);
        services.AddSingleton(_db.ReportedMessages);
        services.AddSingleton(_db.CoreContext.CompanyProfile);
        services.AddSingleton(new ChatStateService());
        services.AddSingleton(new PresenceStateService(new VoiceStateService(), _db.CoreContext, _db.HrContext));
        services.AddSingleton(Mock.Of<ISystemLogService>());
        services.AddSingleton((webPushOverride ?? _mockWebPush).Object);

        if (mockChatAuth != null)
        {
            services.AddSingleton(mockChatAuth.Object);
        }
        else
        {
            var defaultChatAuth = new Mock<IChatAuthorizationService>();
            defaultChatAuth.Setup(c => c.CanUserAccessChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee?>())).Returns(true);
            services.AddSingleton(defaultChatAuth.Object);
        }

        if (mockEmailInbox != null)
        {
            services.AddSingleton(mockEmailInbox.Object);
        }
        else
        {
            var defaultEmailInbox = new Mock<IEmailInboxService>();
            defaultEmailInbox.Setup(e => e.CanUserAccessEmailMessage(It.IsAny<Employee?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            services.AddSingleton(defaultEmailInbox.Object);
        }

        if (pushDecisionOverride != null)
        {
            services.AddSingleton(pushDecisionOverride.Object);
        }
        else
        {
            var defaultDecision = new Mock<IPushDecisionService>();
            defaultDecision.Setup(p => p.ShouldSuppressMobilePush(It.IsAny<string>())).Returns(false);
            services.AddSingleton(defaultDecision.Object);
        }

        var mockRouting = new Mock<INotificationRoutingService>();
        mockRouting.Setup(r => r.GetTotalBadgeCount(It.IsAny<string>())).Returns(1);
        services.AddSingleton(mockRouting.Object);

        var sp = services.BuildServiceProvider();
        return new NotificationQueueBackgroundService(
            _queue,
            sp,
            NullLogger<NotificationQueueBackgroundService>.Instance);
    }

    // =========================================================================
    // #38 — Calendar Authorization, Ownership, Company-Wide & Reminder Guards
    // =========================================================================

    [Fact]
    public void SaveEvent_DeniesCallerWithoutCalendarViewPermission()
    {
        SaveEmployee("no-cal", permissions: [AppPermissions.Chat.Use]);
        var svc = CreateCalendarService();
        var evt = new CalendarEvent
        {
            Id = "evt-no-perm",
            EmployeeId = "no-cal",
            Title = "Unauthorized",
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };

        Assert.Throws<UnauthorizedAccessException>(() => svc.SaveEvent(evt, actingUserId: "no-cal"));
        Assert.Null(_db.CalendarEvents.GetById("evt-no-perm"));
    }

    [Fact]
    public void SaveEvent_DeniesSuspendedUserEvenWithCalendarViewPermission()
    {
        SaveEmployee("susp-cal", isSuspended: true, permissions: [AppPermissions.Calendar.View]);
        var svc = CreateCalendarService();
        var evt = new CalendarEvent
        {
            Id = "evt-susp",
            EmployeeId = "susp-cal",
            Title = "Suspended Write",
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };

        Assert.Throws<UnauthorizedAccessException>(() => svc.SaveEvent(evt, actingUserId: "susp-cal"));
    }

    [Fact]
    public void SaveEvent_DeniesNonAdminCreatingOrEscalatingToCompanyWideEvent()
    {
        var profile = _db.CoreContext.CompanyProfile.Get();
        profile.Privacy.AllowNonAdminsCompanyWideCalendar = false;
        _db.CoreContext.CompanyProfile.Save(profile);

        var admin = SaveEmployee("admin-co", isAdmin: true);
        SaveEmployee("regular", permissions: [AppPermissions.Calendar.View]);
        var svc = CreateCalendarService();

        var companyEvt = new CalendarEvent
        {
            Id = "evt-co-1",
            EmployeeId = "regular",
            Title = "Fake Company Broadcast",
            IsCompanyWide = true,
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };

        Assert.Throws<UnauthorizedAccessException>(() => svc.SaveEvent(companyEvt, actingUserId: "regular"));
        Assert.Null(_db.CalendarEvents.GetById("evt-co-1"));

        var personalEvt = new CalendarEvent
        {
            Id = "evt-co-2",
            EmployeeId = "regular",
            Title = "Personal Event",
            IsCompanyWide = false,
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };
        svc.SaveEvent(personalEvt, actingUserId: "regular");

        var escalated = new CalendarEvent
        {
            Id = "evt-co-2",
            EmployeeId = "regular",
            Title = "Escalated Broadcast",
            IsCompanyWide = true,
            Start = personalEvt.Start,
            End = personalEvt.End
        };
        Assert.Throws<UnauthorizedAccessException>(() => svc.SaveEvent(escalated, actingUserId: "regular"));
        Assert.False(_db.CalendarEvents.GetById("evt-co-2")!.IsCompanyWide);

        var adminCompanyEvt = new CalendarEvent
        {
            Id = "evt-co-admin",
            EmployeeId = admin.Id,
            Title = "Official Announcement",
            IsCompanyWide = true,
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };
        svc.SaveEvent(adminCompanyEvt, actingUserId: admin.Id);
        Assert.True(_db.CalendarEvents.GetById("evt-co-admin")!.IsCompanyWide);
    }

    [Fact]
    public void SaveAndDeleteEvent_DeniesNonOwnerModifyingExistingEvent()
    {
        SaveEmployee("owner", permissions: [AppPermissions.Calendar.View]);
        SaveEmployee("attacker", permissions: [AppPermissions.Calendar.View]);
        var svc = CreateCalendarService();

        var evt = new CalendarEvent
        {
            Id = "evt-priv",
            EmployeeId = "owner",
            Title = "Private 1:1",
            Start = DateTime.UtcNow.AddHours(1),
            End = DateTime.UtcNow.AddHours(2)
        };
        svc.SaveEvent(evt, actingUserId: "owner");

        var tampered = new CalendarEvent
        {
            Id = "evt-priv",
            EmployeeId = "owner",
            Title = "Tampered",
            Start = evt.Start,
            End = evt.End
        };

        Assert.Throws<UnauthorizedAccessException>(() => svc.SaveEvent(tampered, actingUserId: "attacker"));
        Assert.Throws<UnauthorizedAccessException>(() => svc.DeleteEvent("evt-priv", actingUserId: "attacker"));
        Assert.Equal("Private 1:1", _db.CalendarEvents.GetById("evt-priv")!.Title);
    }

    [Fact]
    public void CanUserViewEvent_DeniesUnownedPrivateEventAndUsersWithoutCalendarView()
    {
        var unpermitted = SaveEmployee("no-view");
        var outsider = SaveEmployee("outsider", permissions: [AppPermissions.Calendar.View]);
        var invited = SaveEmployee("invited", permissions: [AppPermissions.Calendar.View]);
        var teamMember = SaveEmployee("team-mem", teamId: "team-1", permissions: [AppPermissions.Calendar.View]);
        var suspendedViewer = SaveEmployee("susp-viewer", isSuspended: true, permissions: [AppPermissions.Calendar.View]);

        _db.Teams.Save(new Team { Id = "team-1", Name = "Eng" });

        // Unowned legacy event (!IsCompanyWide, empty EmployeeId) must NOT leak to random users
        var unownedEvent = new CalendarEvent
        {
            Id = "evt-unowned",
            EmployeeId = string.Empty,
            Title = "Legacy Event",
            IsCompanyWide = false,
            Start = DateTime.UtcNow,
            End = DateTime.UtcNow.AddHours(1)
        };
        var sharedEvent = new CalendarEvent
        {
            Id = "evt-shared",
            EmployeeId = "owner",
            Title = "Team Sync",
            Attendees = ["invited", "no-view", "susp-viewer"],
            InvitedTeamIds = ["team-1"],
            Start = DateTime.UtcNow,
            End = DateTime.UtcNow.AddHours(1)
        };

        var svc = CreateCalendarService();

        Assert.False(svc.CanUserViewEvent(unownedEvent, outsider));
        Assert.False(svc.CanUserViewEvent(sharedEvent, unpermitted));
        Assert.False(svc.CanUserViewEvent(sharedEvent, outsider));
        Assert.True(svc.CanUserViewEvent(sharedEvent, invited));
        Assert.True(svc.CanUserViewEvent(sharedEvent, teamMember, ["team-1"]));
        Assert.True(svc.CanUserViewEvent(sharedEvent, suspendedViewer));
    }

    [Fact]
    public void RsvpAndReminderPreference_DenyUninvitedOrUnpermittedUsers()
    {
        SaveEmployee("owner", permissions: [AppPermissions.Calendar.View]);
        SaveEmployee("outsider", permissions: [AppPermissions.Calendar.View]);
        SaveEmployee("no-cal");
        var svc = CreateCalendarService();

        var evt = new CalendarEvent
        {
            Id = "evt-rsvp-sec",
            EmployeeId = "owner",
            Title = "Exec Review",
            Attendees = ["no-cal"],
            Start = DateTime.UtcNow.AddHours(2),
            End = DateTime.UtcNow.AddHours(3)
        };
        _db.CalendarEvents.Save(evt);

        Assert.False(svc.RsvpToEvent("evt-rsvp-sec", "outsider").IsSuccess);
        Assert.False(svc.RsvpToEvent("evt-rsvp-sec", "no-cal").IsSuccess);
        Assert.False(svc.UpdateUserEventNotificationPreference("evt-rsvp-sec", "outsider", isMuted: true).IsSuccess);
        Assert.False(svc.UpdateUserCategories("no-cal", [new CalendarCategory { Name = "Hacked" }]).IsSuccess);
    }

    [Fact]
    public void SaveAndDeleteEvent_PurgeStaleCalendarNotificationsFromQueue()
    {
        SaveEmployee("owner", permissions: [AppPermissions.Calendar.View]);
        SaveEmployee("guest", permissions: [AppPermissions.Calendar.View]);
        var svc = CreateCalendarService();

        var evt = new CalendarEvent
        {
            Id = "evt-purge-1",
            EmployeeId = "owner",
            Title = "Initial Meeting",
            Attendees = ["guest"],
            Start = DateTime.UtcNow.AddHours(3),
            End = DateTime.UtcNow.AddHours(4)
        };
        svc.SaveEvent(evt, actingUserId: "owner");

        _queue.Enqueue(new QueuedNotification
        {
            UserId = "guest",
            Title = "Initial Meeting",
            Body = "Starts in 10 minutes",
            Category = NotificationCategories.Calendar,
            Url = "/planning/calendar",
            SourceEventId = "evt-purge-1"
        });
        var delayed = new DelayedMobileNotification(
            new PushNotificationRequest
            {
                UserId = "guest",
                Title = "Initial Meeting",
                Body = "Starts in 10 minutes",
                Category = NotificationCategories.Calendar,
                SourceEventId = "evt-purge-1"
            },
            "evt-purge-1",
            "evt-purge-1",
            "Calendar");
        delayed.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(delayed);

        evt.Title = "Rescheduled Meeting";
        svc.SaveEvent(evt, actingUserId: "owner");

        Assert.Empty(_queue.GetQueuedUserIds());
        Assert.Empty(_queue.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(1)));

        _queue.Enqueue(new QueuedNotification
        {
            UserId = "guest",
            Title = "Rescheduled Meeting",
            Body = "Starts soon",
            Category = NotificationCategories.Calendar,
            SourceEventId = "evt-purge-1"
        });
        svc.DeleteEvent("evt-purge-1", actingUserId: "owner");
        Assert.Empty(_queue.GetQueuedUserIds());
    }

    [Fact]
    public async Task CalendarReminderBackgroundService_SkipsUsersWithoutCalendarViewPermission()
    {
        var admin = SaveEmployee("admin-cal", isAdmin: true);
        SaveEmployee("guest-ok", permissions: [AppPermissions.Calendar.View]);
        SaveEmployee("guest-no-perm");
        SaveEmployee("guest-banned", isBanned: true, permissions: [AppPermissions.Calendar.View]);

        var now = TimeZoneHelper.GetCompanyTime(_db.CoreContext.CompanyProfile);
        var evt = new CalendarEvent
        {
            Id = "evt-rem-1",
            EmployeeId = admin.Id,
            Title = "Board Brief",
            IsCompanyWide = true,
            Attendees = ["guest-ok", "guest-no-perm", "guest-banned"],
            Start = now.AddMinutes(5),
            End = now.AddMinutes(65),
            Reminders = [new CalendarReminder { Id = "r1", Value = 10, Unit = CalendarReminderUnit.Minutes }]
        };
        _db.CalendarEvents.Save(evt);

        var sentUsers = new List<string>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockPush = new Mock<IWebPushService>();
        mockPush.SetupGet(w => w.IsConfigured).Returns(true);
        mockPush.Setup(w => w.SendNotificationDirectAsync(It.IsAny<PushNotificationRequest>()))
            .Callback<PushNotificationRequest>(req =>
            {
                lock (sentUsers)
                {
                    sentUsers.Add(req.UserId);
                    if (sentUsers.Count >= 2)
                    {
                        tcs.TrySetResult();
                    }
                }
            })
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_db.HrContext);
        services.AddSingleton(_db.CoreContext.CompanyProfile);
        services.AddSingleton(_db.Teams);
        services.AddSingleton(_queue);
        services.AddSingleton(mockPush.Object);
        using var sp = services.BuildServiceProvider();

        var bg = new CalendarReminderBackgroundService(sp, _reminderState, NullLogger<CalendarReminderBackgroundService>.Instance);
        await bg.CheckDueRemindersAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.True(_reminderState.HasBeenDispatched(evt.Id, "r1", "guest-ok", evt.Start.Ticks));
        Assert.True(_reminderState.HasBeenDispatched(evt.Id, "r1", admin.Id, evt.Start.Ticks));
        Assert.False(_reminderState.HasBeenDispatched(evt.Id, "r1", "guest-no-perm", evt.Start.Ticks));
        Assert.False(_reminderState.HasBeenDispatched(evt.Id, "r1", "guest-banned", evt.Start.Ticks));
        Assert.DoesNotContain("guest-no-perm", sentUsers);
        Assert.DoesNotContain("guest-banned", sentUsers);
    }

    [Fact]
    public async Task CalendarNotificationService_SuppressesAndDetachesIneligibleUsers()
    {
        SaveEmployee("no-cal-ui");
        SaveEmployee("yes-cal-ui", permissions: [AppPermissions.Calendar.View]);

        var mockPresenter = new Mock<IUiNotificationPresenter>();
        var mockSound = new Mock<ISoundService>();
        mockSound.Setup(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var circuit = new UserCircuitContext { IsFocused = true, IsConnected = true };
        var svc = new CalendarNotificationService(
            mockPresenter.Object,
            mockSound.Object,
            _reminderState,
            _db.CoreContext.CompanyProfile,
            circuit,
            null,
            null,
            _db.Employees);

        var evt = new CalendarEvent { Id = "evt-ui", Title = "Private Event", Start = DateTime.UtcNow.AddMinutes(10), End = DateTime.UtcNow.AddHours(1) };
        var rem = new CalendarReminder { Id = "r-ui", Value = 10, Unit = CalendarReminderUnit.Minutes };

        await svc.InitializeAsync("no-cal-ui");
        _reminderState.TriggerReminder("no-cal-ui", evt, rem);
        mockPresenter.Verify(p => p.ShowCalendarNotification(It.IsAny<CalendarNotificationModel>()), Times.Never);

        await svc.InitializeAsync("yes-cal-ui");
        _reminderState.TriggerReminder("yes-cal-ui", evt, rem);
        mockPresenter.Verify(p => p.ShowCalendarNotification(It.IsAny<CalendarNotificationModel>()), Times.Once);

        await svc.DisposeAsync();
    }

    // =========================================================================
    // #39 — Notification Queue Stale State Revalidation & Other Category Fix
    // =========================================================================

    [Fact]
    public async Task NotificationQueueBackgroundService_DropsStaleUnauthorizedOrAlreadyReadNotifications()
    {
        var chatUser = SaveEmployee("chat-user", permissions: [AppPermissions.Chat.Use]);
        var revokedChatUser = SaveEmployee("revoked-chat", permissions: [AppPermissions.Chat.Use]);
        var emailUser = SaveEmployee("email-user", permissions: [AppPermissions.Email.Use]);
        var modUser = SaveEmployee("mod-user", isAdmin: false);
        var calUser = SaveEmployee("cal-user"); // Missing Calendar.View
        var otherUser = SaveEmployee("other-user", permissions: [AppPermissions.Chat.Use]);

        var channelAllowed = new ChatChannel { Id = "ch-ok", Name = "Allowed" };
        var channelRevoked = new ChatChannel { Id = "ch-revoked", Name = "Secret" };
        _db.ChatChannels.Save(channelAllowed);
        _db.ChatChannels.Save(channelRevoked);

        var msgTime = DateTime.UtcNow.AddMinutes(-2);
        _db.ChatMessages.Save(new ChatMessage { Id = "m-deleted", ChannelId = "ch-ok", SenderId = "sender", Content = "hi", IsDeleted = true, SentAt = msgTime });
        _db.ChatMessages.Save(new ChatMessage { Id = "m-read", ChannelId = "ch-ok", SenderId = "sender", Content = "hi", SentAt = msgTime });
        _db.ChatMessages.Save(new ChatMessage { Id = "m-revoked", ChannelId = "ch-revoked", SenderId = "sender", Content = "hi", SentAt = msgTime });
        _db.ChatReadStates.Save(new ChatReadState { Id = $"{chatUser.Id}_ch-ok", UserId = chatUser.Id, ChannelId = "ch-ok", LastReadAt = DateTime.UtcNow });

        _db.EmailMessages.Save(new EmailMessage { Id = "em-read", AccountId = "acc-1", FolderPath = "INBOX", Subject = "Read", IsRead = true });
        _db.EmailMessages.Save(new EmailMessage { Id = "em-revoked", AccountId = "acc-1", FolderPath = "INBOX", Subject = "Revoked", IsRead = false });
        _db.ReportedMessages.Save(new ReportedMessage { Id = "rep-resolved", ChannelId = "ch-ok", MessageId = "m-read", Status = ReportedMessageStatus.Resolved });
        _db.CalendarEvents.Save(new CalendarEvent { Id = "evt-no-cal", EmployeeId = calUser.Id, Title = "No Perm", Start = DateTime.UtcNow.AddMinutes(5), End = DateTime.UtcNow.AddHours(1) });

        var mockChatAuth = new Mock<IChatAuthorizationService>();
        mockChatAuth.Setup(c => c.CanUserAccessChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee?>()))
            .Returns((ChatChannel ch, Employee? emp) => ch.Id == "ch-ok" && emp?.Id == chatUser.Id);

        var mockEmailInbox = new Mock<IEmailInboxService>();
        mockEmailInbox.Setup(e => e.CanUserAccessEmailMessage(It.IsAny<Employee?>(), "acc-1", "em-read")).Returns(true);
        mockEmailInbox.Setup(e => e.CanUserAccessEmailMessage(It.IsAny<Employee?>(), "acc-1", "em-revoked")).Returns(false);

        // 1. Deleted chat message (queued)
        _queue.Enqueue(new QueuedNotification(new PushNotificationRequest
        {
            UserId = chatUser.Id,
            Title = "Deleted",
            Body = "Deleted msg body",
            Category = NotificationCategories.Chat,
            SourceChannelId = "ch-ok",
            SourceMessageId = "m-deleted",
            SentAtUtc = msgTime
        }));

        // 2. Already read chat message (delayed mobile)
        var dRead = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = chatUser.Id,
            Title = "Read Msg",
            Body = "Already read",
            Category = NotificationCategories.Chat,
            SourceChannelId = "ch-ok",
            SourceMessageId = "m-read",
            SentAtUtc = msgTime
        }, "m-read", "ch-ok", "Chat");
        dRead.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dRead);

        // 3. Revoked channel access (delayed mobile)
        var dRevokedChat = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = revokedChatUser.Id,
            Title = "Revoked Chat",
            Body = "No longer accessible",
            Category = NotificationCategories.Chat,
            SourceChannelId = "ch-revoked",
            SourceMessageId = "m-revoked",
            SentAtUtc = msgTime
        }, "m-revoked", "ch-revoked", "Chat");
        dRevokedChat.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dRevokedChat);

        // 4. Already read email & revoked email access (delayed mobile)
        var dEmailRead = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = emailUser.Id,
            Title = "Read Email",
            Body = "Already read email",
            Category = NotificationCategories.Email,
            SourceEmailAccountId = "acc-1",
            SourceEmailFolderPath = "INBOX",
            SourceEmailMessageId = "em-read"
        }, "em-read", "acc-1", "Email", accountId: "acc-1", folderPath: "INBOX");
        dEmailRead.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dEmailRead);

        var dEmailRevoked = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = emailUser.Id,
            Title = "Revoked Email",
            Body = "No longer accessible",
            Category = NotificationCategories.Email,
            SourceEmailAccountId = "acc-1",
            SourceEmailFolderPath = "INBOX",
            SourceEmailMessageId = "em-revoked"
        }, "em-revoked", "acc-1", "Email", accountId: "acc-1", folderPath: "INBOX");
        dEmailRevoked.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dEmailRevoked);

        // 5. Resolved report / non-admin moderation target (delayed mobile)
        var dMod = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = modUser.Id,
            Title = "Mod Alert",
            Body = "Mod body",
            Category = "moderation",
            SourceReportId = "rep-resolved"
        }, "rep-resolved", "ch-ok", "Moderation");
        dMod.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dMod);

        // 6. Calendar item where user lacks Calendar.View
        var dCal = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = calUser.Id,
            Title = "Calendar Alert",
            Body = "Starts soon",
            Category = NotificationCategories.Calendar,
            SourceEventId = "evt-no-cal"
        }, "evt-no-cal", "evt-no-cal", "Calendar");
        dCal.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dCal);

        // 7. Regression check: DelayedMobileNotification with Type = "Other" (even though Request.Category defaults to "chat")
        // MUST NOT be misclassified as Chat and dropped due to missing channel.
        var dOther = new DelayedMobileNotification(new PushNotificationRequest
        {
            UserId = otherUser.Id,
            Title = "Custom System Notice",
            Body = "Legit custom notification"
        }, "other-1", "nonexistent-channel", "Other");
        dOther.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(dOther);

        var dispatched = new List<PushNotificationRequest>();
        var pushMock = new Mock<IWebPushService>();
        pushMock.SetupGet(w => w.IsConfigured).Returns(true);
        pushMock.Setup(w => w.SendNotificationDirectAsync(It.IsAny<PushNotificationRequest>()))
            .Callback<PushNotificationRequest>(r => dispatched.Add(r))
            .Returns(Task.CompletedTask);

        var bg = CreateQueueBackgroundService(mockChatAuth, mockEmailInbox, pushMock);
        await bg.StartAsync(CancellationToken.None);
        await bg.StopAsync(CancellationToken.None);

        Assert.Single(dispatched);
        Assert.Equal("other-user", dispatched[0].UserId);
        Assert.Equal("Custom System Notice", dispatched[0].Title);
        Assert.Empty(_queue.GetQueuedUserIds());
        Assert.Empty(_queue.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void NotificationQueueService_PurgeMethods_RemoveFromBothQueuedAndDelayedMobileQueues()
    {
        _queue.Enqueue(new QueuedNotification(new PushNotificationRequest
        {
            UserId = "u1",
            Title = "Msg",
            Body = "Body 1",
            SourceChannelId = "ch-1",
            SourceMessageId = "msg-1"
        }));
        _queue.Enqueue(new QueuedNotification(new PushNotificationRequest
        {
            UserId = "u1",
            Title = "Mod",
            Body = "Body 2",
            Category = "moderation",
            SourceReportId = "rep-1"
        }));
        _queue.Enqueue(new QueuedNotification(new PushNotificationRequest
        {
            UserId = "u2",
            Title = "Keep",
            Body = "Body Keep",
            SourceChannelId = "ch-other",
            SourceMessageId = "msg-keep"
        }));

        var delayedMsg = new DelayedMobileNotification(
            new PushNotificationRequest { UserId = "u1", Title = "Delayed Msg", Body = "Delayed 1" },
            "msg-1",
            "ch-1",
            "Chat");
        delayedMsg.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(delayedMsg);

        var delayedMod = new DelayedMobileNotification(
            new PushNotificationRequest { UserId = "u1", Title = "Delayed Mod", Body = "Delayed 2", Category = "moderation" },
            "rep-1",
            "ch-1",
            "Moderation");
        delayedMod.ProcessAtUtc = DateTime.UtcNow.AddSeconds(-5);
        _queue.EnqueueDelayedMobilePush(delayedMod);

        Assert.Equal(2, _queue.PurgeForChatMessage("ch-1", "msg-1"));
        Assert.Equal(2, _queue.PurgeForModerationReport("rep-1"));

        Assert.DoesNotContain("u1", _queue.GetQueuedUserIds());
        Assert.Contains("u2", _queue.GetQueuedUserIds());
        Assert.Empty(_queue.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(1)));
    }

    // =========================================================================
    // #78 — NativeRelay RSA-2048+ Validation, License Gating & Downgrade Safety
    // =========================================================================

    [Fact]
    public void PushPayloadBuilder_TryValidateRsaPublicKey_RejectsWeakInvalidAndAcceptsRsa2048Plus()
    {
        Assert.False(PushPayloadBuilder.IsValidRsaPublicKey(null));
        Assert.False(PushPayloadBuilder.IsValidRsaPublicKey(""));
        Assert.False(PushPayloadBuilder.IsValidRsaPublicKey("not-base64!!!"));
        Assert.False(PushPayloadBuilder.IsValidRsaPublicKey(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));

        var (weak1024Pub, weakKey) = CreateRsaPair(1024);
        using (weakKey)
        {
            Assert.False(PushPayloadBuilder.TryValidateRsaPublicKey(weak1024Pub, out var parsedWeak, out var weakError));
            Assert.Null(parsedWeak);
            Assert.NotNull(weakError);
        }

        var (valid2048Pub, validKey) = CreateRsaPair(2048);
        using (validKey)
        {
            Assert.True(PushPayloadBuilder.TryValidateRsaPublicKey(valid2048Pub, out var parsedValid, out var validError));
            Assert.NotNull(parsedValid);
            Assert.Null(validError);
            parsedValid!.Dispose();
        }
    }

    [Fact]
    public void PushController_Subscribe_NativeRelay_RejectsInvalidOrUnlicensedBeforeModifyingExistingSession()
    {
        var user = SaveEmployee("relay-user");
        var (validPub, validPrivate) = CreateRsaPair(2048);
        using var _ = validPrivate;

        var existing = new DeviceSession
        {
            Id = "sess-existing",
            EmployeeId = user.Id,
            DeviceId = "dev-1",
            TokenHash = "hash-1",
            PushEndpoint = "fcm-token-1",
            PushSubscriptionType = PushSubscriptionType.NativeRelay,
            PushPublicKey = validPub,
            PushEnabled = true
        };
        _db.DeviceSessions.Save(existing);

        var callerSession = new DeviceSession
        {
            Id = "sess-caller",
            EmployeeId = user.Id,
            DeviceId = "dev-other",
            TokenHash = "hash-2"
        };
        _db.DeviceSessions.Save(callerSession);

        // Case 1: Invalid / weak RSA key (1024-bit) rejected before dedup clearing
        var (weakPub, weakPrivate) = CreateRsaPair(1024);
        using var __ = weakPrivate;
        var ctrlLicensed = CreatePushController(user.Id, nativePushAllowed: true);

        var weakResult = ctrlLicensed.Subscribe(new SubscribeRequest
        {
            Endpoint = "fcm-token-1",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev-other",
            PublicKey = weakPub
        });

        Assert.IsType<BadRequestObjectResult>(weakResult);
        var afterWeak = _db.DeviceSessions.GetById("sess-existing");
        Assert.NotNull(afterWeak);
        Assert.Equal("fcm-token-1", afterWeak!.PushEndpoint);
        Assert.Equal(validPub, afterWeak.PushPublicKey);

        // Case 2: Unlicensed NativeRelay rejected before touching existing session
        var ctrlUnlicensed = CreatePushController(user.Id, nativePushAllowed: false);
        var unlicensedResult = ctrlUnlicensed.Subscribe(new SubscribeRequest
        {
            Endpoint = "fcm-token-1",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev-other",
            PublicKey = validPub
        });

        var statusCode = Assert.IsType<ObjectResult>(unlicensedResult).StatusCode;
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        var afterUnlicensed = _db.DeviceSessions.GetById("sess-existing");
        Assert.NotNull(afterUnlicensed);
        Assert.Equal(validPub, afterUnlicensed!.PushPublicKey);

        // Case 3: Refreshing same NativeRelay session with blank PublicKey preserves registered RSA key
        var refreshResult = ctrlLicensed.Subscribe(new SubscribeRequest
        {
            Endpoint = "fcm-token-1",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev-1",
            PublicKey = "   "
        });
        Assert.IsType<OkObjectResult>(refreshResult);
        Assert.Equal(validPub, _db.DeviceSessions.GetById("sess-existing")!.PushPublicKey);

        // Case 4: Switching session to WebPush clears PushPublicKey
        var switchResult = ctrlLicensed.Subscribe(new SubscribeRequest
        {
            Endpoint = "https://push.example/sub/123",
            P256dh = "p256",
            Auth = "auth",
            SubscriptionType = PushSubscriptionType.WebPush,
            DeviceId = "dev-1"
        });
        Assert.IsType<OkObjectResult>(switchResult);
        var afterSwitch = _db.DeviceSessions.GetById("sess-existing");
        Assert.NotNull(afterSwitch);
        Assert.Equal(PushSubscriptionType.WebPush, afterSwitch!.PushSubscriptionType);
        Assert.Equal(string.Empty, afterSwitch.PushPublicKey);
    }

    // =========================================================================
    // NV-8 — Content-Free Wakeup Fallback & Zero Outer Plaintext Leakage
    // =========================================================================

    [Fact]
    public void PushPayloadBuilder_BuildRelayPayload_EmitsContentFreeWakeupWhenKeyMissingOrInvalid()
    {
        var builder = new PushPayloadBuilder();
        var context = new PushRelayContext(
            new CompanyProfile { CompanyName = "Top Secret Corp" },
            new ServerConfig { Id = "srv-1" },
            "/icon-192.png",
            "https://spokes.internal");

        var request = new PushNotificationRequest
        {
            UserId = "u1",
            Title = "Confidential M&A Offer",
            Body = "Wire $5,000,000 to escrow today",
            Url = "/chat/ch-secret",
            Icon = "/spokesapi/file/avatar.png?t=secrettoken",
            ChannelName = "Executive Deal Room",
            ServerName = "Top Secret Corp",
            Category = "chat",
            ThreadId = "ch-secret",
            IsGroupChat = true
        };

        var missingKeySession = new DeviceSession
        {
            PushEndpoint = "fcm-tok",
            PushSubscriptionType = PushSubscriptionType.NativeRelay,
            PushPublicKey = ""
        };

        var wakeup = builder.BuildRelayPayload(request, missingKeySession, context);

        Assert.False(wakeup.IsEncrypted);
        Assert.Null(wakeup.EncryptedKey);
        Assert.Null(wakeup.Nonce);
        Assert.Null(wakeup.Ciphertext);
        Assert.Null(wakeup.Tag);
        Assert.Equal("New notification", wakeup.Title);
        Assert.Equal("Open Spokes to view.", wakeup.Body);
        Assert.Null(wakeup.Url);
        Assert.Null(wakeup.ImageUrl);
        Assert.Null(wakeup.ServerName);
        Assert.Null(wakeup.ChannelName);
        Assert.Null(wakeup.OriginUrl);
        Assert.Null(wakeup.ServerIconUrl);
        Assert.False(wakeup.IsGroupChat);

        // Weak 1024-bit RSA key must ALSO fall back to content-free wakeup
        var (weak1024Pub, weakKey) = CreateRsaPair(1024);
        using (weakKey)
        {
            var weakSession = new DeviceSession
            {
                PushEndpoint = "fcm-tok-weak",
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                PushPublicKey = weak1024Pub
            };
            var weakWakeup = builder.BuildRelayPayload(request, weakSession, context);
            Assert.False(weakWakeup.IsEncrypted);
            Assert.Equal("New notification", weakWakeup.Title);
            Assert.Equal("Open Spokes to view.", weakWakeup.Body);
            Assert.Null(weakWakeup.Url);
            Assert.Null(weakWakeup.ImageUrl);
        }

        // Valid RSA-2048+ key encrypts sensitive fields and strips them from outer payload
        var (validPub, privateKey) = CreateRsaPair(2048);
        using (privateKey)
        {
            var validSession = new DeviceSession
            {
                PushEndpoint = "fcm-tok-ok",
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                PushPublicKey = validPub,
                HasVaultCookie = true
            };
            var encrypted = builder.BuildRelayPayload(request, validSession, context);
            Assert.True(encrypted.IsEncrypted);
            Assert.Null(encrypted.Title);
            Assert.Null(encrypted.Body);
            Assert.Null(encrypted.Url);
            Assert.Null(encrypted.ImageUrl);
            Assert.Null(encrypted.ChannelName);

            var innerJson = DecryptRelayInnerPayload(encrypted, privateKey);
            using var doc = JsonDocument.Parse(innerJson);
            Assert.Equal("Confidential M&A Offer", doc.RootElement.GetProperty("Title").GetString());
            Assert.Equal("Wire $5,000,000 to escrow today", doc.RootElement.GetProperty("Body").GetString());
            Assert.Equal("Executive Deal Room", doc.RootElement.GetProperty("ChannelName").GetString());
        }
    }
}
