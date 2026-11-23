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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class ChatMessageDeleteConfirmationTests : BunitContext
{
    private readonly string _testDataDir;
    private readonly Database _db;
    private readonly Employee _user;
    private readonly ChatChannel _channel;
    private readonly UserCircuitContext _circuitContext;

    public ChatMessageDeleteConfirmationTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_ChatDeleteConfirm_{Guid.NewGuid():N}");
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

        var userId = "emp-chat-del-1";
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("EmployeeId", userId),
                new Claim("SessionId", "sess-chat-del-1")
            ],
            authenticationType: "TestAuth");
        var authState = new AuthenticationState(new ClaimsPrincipal(identity));
        var authProviderMock = new Mock<AuthenticationStateProvider>();
        authProviderMock.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(authState);
        Services.AddSingleton<AuthenticationStateProvider>(authProviderMock.Object);

        Services.AddScoped<UserCircuitContext>();
        Services.AddScoped<StartupTimingService>();

        _db = Services.GetRequiredService<Database>();
        _circuitContext = Services.GetRequiredService<UserCircuitContext>();
        _circuitContext.SessionId = "sess-chat-del-1";

        _user = new Employee
        {
            Id = userId,
            FirstName = "Alice",
            LastName = "Tester",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        _db.Employees.Save(_user);
        _db.DeviceSessions.Save(new Spokes_Server.Core.Models.Core.DeviceSession
        {
            Id = "sess-chat-del-1",
            EmployeeId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });

        _channel = new ChatChannel
        {
            Id = "ch-del-confirm",
            Name = "General",
            ChannelType = ChatChannelType.General,
            CategoryId = "sys_public"
        };
        _db.ChatChannels.Save(_channel);
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

    private ChatMessage SeedMessage(string id, string content)
    {
        var msg = new ChatMessage
        {
            Id = id,
            ChannelId = _channel.Id,
            SenderId = _user.Id,
            Content = content,
            SentAt = DateTime.UtcNow
        };
        _db.ChatMessages.Save(msg);
        return msg;
    }

    [Fact]
    public async Task DesktopHoverMenu_RendersHiddenDivPrototype_AndSwapsActionSlotOnPopupToggle()
    {
        var msg = SeedMessage("msg-desktop-hover-1", "Desktop hover toolbar regression test");
        _circuitContext.IsMobileViewport = false;

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Chat.Chat>(p => p
            .Add(x => x.ChannelId, _channel.Id));

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg.Id}']")));

        // Prototype must be a hidden light-DOM div (never <template>, whose .content is empty in interactive Blazor)
        var prototype = cut.Find("#spokes-desktop-message-actions-template");
        Assert.Equal("DIV", prototype.TagName);
        Assert.True(prototype.HasAttribute("hidden"));
        Assert.Equal("true", prototype.GetAttribute("aria-hidden"));
        Assert.NotNull(prototype.FirstElementChild);
        Assert.Equal("1", prototype.FirstElementChild!.GetAttribute("data-spokes-shared-actions"));

        // Idle desktop row renders the dedicated JS mount slot and no Blazor popup toolbar
        var row = cut.Find($"[data-message-id='{msg.Id}']");
        Assert.NotNull(row.QuerySelector(".message-actions-stack > .message-actions-slot"));
        Assert.Null(row.QuerySelector(".message-actions.force-visible"));

        // Opening the '...' popup atomically swaps .message-actions-slot out for Blazor's .message-actions.force-visible
        await cut.InvokeAsync(() => cut.Instance.HandleDesktopMessageActionJS(msg.Id, "more", null));
        cut.WaitForAssertion(() =>
        {
            var activeRow = cut.Find($"[data-message-id='{msg.Id}']");
            Assert.Null(activeRow.QuerySelector(".message-actions-slot"));
            Assert.NotNull(activeRow.QuerySelector(".message-actions-stack > .message-actions.force-visible"));
        });

        // Toggling '...' closed restores the idle JS mount slot
        await cut.InvokeAsync(() => cut.Instance.HandleDesktopMessageActionJS(msg.Id, "more", null));
        cut.WaitForAssertion(() =>
        {
            var closedRow = cut.Find($"[data-message-id='{msg.Id}']");
            Assert.NotNull(closedRow.QuerySelector(".message-actions-stack > .message-actions-slot"));
            Assert.Null(closedRow.QuerySelector(".message-actions.force-visible"));
        });
    }

    [Fact]
    public async Task DesktopMoreMenu_Delete_PromptsConfirmation_CancelPreservesMessage_ConfirmDeletesMessage()
    {
        var msg = SeedMessage("msg-desktop-1", "Desktop message requiring delete confirmation");
        _circuitContext.IsMobileViewport = false;

        var popoverProvider = Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Chat.Chat>(p => p
            .Add(x => x.ChannelId, _channel.Id));

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg.Id}']")));

        // 1. Open desktop '...' more menu and click Delete
        await cut.InvokeAsync(() => cut.Instance.HandleDesktopMessageActionJS(msg.Id, "more", null));
        popoverProvider.WaitForAssertion(() =>
            Assert.NotEmpty(popoverProvider.FindAll(".mud-popover-open .mud-list-item")));

        var deleteMenuItem = popoverProvider.FindAll(".mud-popover-open .mud-list-item")
            .First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal));
        await popoverProvider.InvokeAsync(() => deleteMenuItem.Click());

        // 2. Confirmation dialog must be visible and message must NOT yet be deleted
        dialogProvider.WaitForAssertion(() =>
        {
            var box = dialogProvider.Find(".mud-message-box");
            Assert.Contains("Delete Message", box.TextContent, StringComparison.Ordinal);
            Assert.Contains("Are you sure you want to delete this message?", box.TextContent, StringComparison.Ordinal);
        });
        Assert.False(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);

        // 3. Clicking Cancel must close the dialog and leave the message intact
        var cancelBtn = dialogProvider.FindAll(".mud-message-box button")
            .First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal));
        await dialogProvider.InvokeAsync(() => cancelBtn.Click());

        dialogProvider.WaitForAssertion(() => Assert.Empty(dialogProvider.FindAll(".mud-message-box")));
        Assert.False(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);
        Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg.Id}']"));

        // 4. Reopen '...' menu, click Delete, and confirm in dialog
        await cut.InvokeAsync(() => cut.Instance.HandleDesktopMessageActionJS(msg.Id, "more", null));
        popoverProvider.WaitForAssertion(() =>
            Assert.NotEmpty(popoverProvider.FindAll(".mud-popover-open .mud-list-item")));

        deleteMenuItem = popoverProvider.FindAll(".mud-popover-open .mud-list-item")
            .First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal));
        await popoverProvider.InvokeAsync(() => deleteMenuItem.Click());

        dialogProvider.WaitForAssertion(() =>
            Assert.NotEmpty(dialogProvider.FindAll(".mud-message-box button")));

        var confirmDeleteBtn = dialogProvider.FindAll(".mud-message-box button")
            .Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
        await dialogProvider.InvokeAsync(() => confirmDeleteBtn.Click());

        // 5. Message is now deleted in storage and removed from the feed
        Assert.True(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);
        cut.WaitForAssertion(() =>
            Assert.Empty(cut.FindAll($"[data-message-id='{msg.Id}']")));
    }

    [Fact]
    public async Task MobileActionSheet_Delete_PromptsConfirmation_CancelPreservesMessage_ConfirmDeletesMessage()
    {
        var msg = SeedMessage("msg-mobile-1", "Mobile message requiring delete confirmation");
        _circuitContext.IsMobileViewport = true;

        Render<MudPopoverProvider>();
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Chat.Chat>(p => p
            .Add(x => x.ChannelId, _channel.Id));

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg.Id}']")));

        // 1. Trigger mobile long-press action sheet and click Delete
        await cut.InvokeAsync(() => cut.Instance.OnMobileLongPress(msg.Id));
        cut.WaitForAssertion(() =>
            Assert.Contains("Delete", cut.Find(".action-sheet").TextContent, StringComparison.Ordinal));

        var sheetDeleteItem = cut.FindAll(".action-sheet .mud-list-item")
            .First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal));
        await cut.InvokeAsync(() => sheetDeleteItem.Click());

        // 2. Confirmation dialog appears and message is NOT deleted yet
        dialogProvider.WaitForAssertion(() =>
            Assert.Contains("Delete Message", dialogProvider.Find(".mud-message-box").TextContent, StringComparison.Ordinal));
        Assert.False(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);

        // 3. Cancel preserves message
        var cancelBtn = dialogProvider.FindAll(".mud-message-box button")
            .First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal));
        await dialogProvider.InvokeAsync(() => cancelBtn.Click());

        dialogProvider.WaitForAssertion(() => Assert.Empty(dialogProvider.FindAll(".mud-message-box")));
        Assert.False(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);

        // 4. Reopen action sheet, click Delete, and confirm
        await cut.InvokeAsync(() => cut.Instance.OnMobileLongPress(msg.Id));
        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll(".action-sheet .mud-list-item")));

        sheetDeleteItem = cut.FindAll(".action-sheet .mud-list-item")
            .First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal));
        await cut.InvokeAsync(() => sheetDeleteItem.Click());

        dialogProvider.WaitForAssertion(() =>
            Assert.NotEmpty(dialogProvider.FindAll(".mud-message-box button")));

        var confirmDeleteBtn = dialogProvider.FindAll(".mud-message-box button")
            .Last(b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
        await dialogProvider.InvokeAsync(() => confirmDeleteBtn.Click());

        // 5. Message is deleted in storage and removed from the feed
        Assert.True(_db.ChatMessages.GetById(msg.Id)!.IsDeleted);
        cut.WaitForAssertion(() =>
            Assert.Empty(cut.FindAll($"[data-message-id='{msg.Id}']")));
    }

    [Fact]
    public async Task MobileActionSheet_HighlightsOnlyTargetedMessageRow_AndClearsOnClose()
    {
        var msg1 = SeedMessage("msg-mobile-hl-1", "First mobile message");
        var msg2 = SeedMessage("msg-mobile-hl-2", "Second mobile message");
        _circuitContext.IsMobileViewport = true;

        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        var cut = Render<Spokes_Server.Components.Pages.Chat.Chat>(p => p
            .Add(x => x.ChannelId, _channel.Id));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg1.Id}']"));
            Assert.NotEmpty(cut.FindAll($"[data-message-id='{msg2.Id}']"));
        });

        // Initially neither row is marked as the action-sheet target
        Assert.DoesNotContain("message-row-action-target", cut.Find($"[data-message-id='{msg1.Id}']").ClassList);
        Assert.DoesNotContain("message-row-action-target", cut.Find($"[data-message-id='{msg2.Id}']").ClassList);

        // Long-pressing msg1 highlights ONLY msg1 while the action sheet is open
        await cut.InvokeAsync(() => cut.Instance.OnMobileLongPress(msg1.Id));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("message-row-action-target", cut.Find($"[data-message-id='{msg1.Id}']").ClassList);
            Assert.DoesNotContain("message-row-action-target", cut.Find($"[data-message-id='{msg2.Id}']").ClassList);
            Assert.NotEmpty(cut.FindAll(".action-sheet-backdrop"));
        });

        // Dismissing the action sheet via backdrop clears the highlight cleanly
        await cut.InvokeAsync(() => cut.Find(".action-sheet-backdrop").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".action-sheet-backdrop"));
            Assert.DoesNotContain("message-row-action-target", cut.Find($"[data-message-id='{msg1.Id}']").ClassList);
            Assert.DoesNotContain("message-row-action-target", cut.Find($"[data-message-id='{msg2.Id}']").ClassList);
        });
    }
}
