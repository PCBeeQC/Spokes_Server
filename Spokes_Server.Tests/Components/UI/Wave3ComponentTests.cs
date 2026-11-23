using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Components.Shared;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class Wave3ComponentTests : BunitContext
{
    public Wave3ComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddSingleton(new PresenceStateService(new VoiceStateService()));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    [Fact]
    public void LazyMudMenu_DoesNotMountOverlayOrPopoverUntilClicked_AndClosesCleanly()
    {
        var popoverProvider = Render<MudPopoverProvider>();
        bool itemClicked = false;

        var cut = Render<LazyMudMenu>(parameters => parameters
            .Add(p => p.Icon, Icons.Material.Filled.MoreVert)
            .Add(p => p.Title, "Options")
            .Add(p => p.ChildContent, (Action closeMenu) => (RenderFragment)(builder =>
            {
                builder.OpenComponent<MudListItem<string>>(0);
                builder.AddAttribute(1, "Class", "mud-menu-item");
                builder.AddAttribute(2, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () =>
                {
                    itemClicked = true;
                    closeMenu();
                }));
                builder.AddAttribute(3, "ChildContent", (RenderFragment)(b => b.AddContent(0, "Settings")));
                builder.CloseComponent();
            })));

        // Closed state: canonical .mud-menu + activator button present, ZERO overlays/popovers mounted
        Assert.NotNull(cut.Find(".mud-menu"));
        var activator = cut.Find(".mud-menu-icon-button-activator");
        Assert.Equal("false", activator.GetAttribute("aria-expanded"));
        Assert.Empty(popoverProvider.FindAll(".mud-overlay"));
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));

        // Click activator -> mounts overlay & open popover with .mud-menu-item
        activator.Click();
        Assert.Equal("true", cut.Find(".mud-menu-icon-button-activator").GetAttribute("aria-expanded"));
        Assert.Single(popoverProvider.FindAll(".mud-overlay"));
        var openPopover = popoverProvider.Find(".mud-popover-open");
        var menuItem = openPopover.QuerySelector(".mud-menu-item");
        Assert.NotNull(menuItem);
        Assert.Contains("Settings", menuItem!.TextContent);

        // Clicking the item invokes handler and unmounts overlay/popover again
        popoverProvider.Find(".mud-popover-open .mud-menu-item").Click();
        Assert.True(itemClicked);
        Assert.Equal("false", cut.Find(".mud-menu-icon-button-activator").GetAttribute("aria-expanded"));
        Assert.Empty(popoverProvider.FindAll(".mud-overlay"));
        Assert.Empty(popoverProvider.FindAll(".mud-popover-open"));
    }

    [Fact]
    public void MessageBubble_IdleDesktopRow_EmitsEmptyActionsStack_AndMountsPopupOnlyWhenActive()
    {
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        var voiceJsMock = new Mock<IVoiceJsInteropService>();
        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var voiceChannelMock = new Mock<VoiceChannelService>();

        Services.AddLogging();
        Services.AddMemoryCache();
        Services.AddDataProtection();
        Services.AddSingleton<MarkdownSanitizerService>();
        Services.AddSingleton(new LinkPreviewService(httpFactoryMock.Object));
        Services.AddSingleton<FileTokenService>();
        Services.AddSingleton<ImageRecoveryService>();
        Services.AddScoped<UserCircuitContext>();
        Services.AddSingleton(new GlobalVoiceService(
            voiceJsMock.Object,
            voiceChannelMock.Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance));

        var popoverProvider = Render<MudPopoverProvider>();
        var sender = new Employee { Id = "u1", FirstName = "Ada", LastName = "Lovelace" };
        var message = new ChatMessage
        {
            Id = "msg-1",
            ChannelId = "general",
            SenderId = "u1",
            Content = "Hello Wave 5",
            SentAt = DateTime.UtcNow
        };

        var cut = Render<MessageBubble>(p => p
            .Add(x => x.Message, message)
            .Add(x => x.Sender, sender)
            .Add(x => x.CurrentUserId, "u1")
            .Add(x => x.IsOwnMessage, true)
            .Add(x => x.IsMobile, false)
            .Add(x => x.ShowEmojiPicker, false)
            .Add(x => x.ShowMoreMenu, false));

        // Idle desktop row: empty .message-actions-stack slot, zero per-row toolbar buttons serialized
        var stack = cut.Find(".message-actions-stack");
        Assert.Empty(stack.QuerySelectorAll(".message-actions"));
        Assert.Empty(cut.FindAll(".message-action-btn"));

        // Active popup row: Blazor mounts .message-actions.force-visible and popover menu items
        cut.Render(p => p
            .Add(x => x.Message, message)
            .Add(x => x.Sender, sender)
            .Add(x => x.CurrentUserId, "u1")
            .Add(x => x.IsOwnMessage, true)
            .Add(x => x.IsMobile, false)
            .Add(x => x.ShowEmojiPicker, false)
            .Add(x => x.ShowMoreMenu, true));

        Assert.NotNull(cut.Find(".message-actions-stack .message-actions.force-visible"));
        Assert.NotEmpty(cut.FindAll(".message-action-btn"));
        Assert.NotEmpty(popoverProvider.FindAll(".mud-popover-open"));
    }

    [Fact]
    public async Task GlobalVoiceBar_SyncPullDrawerJs_SkipsConnecting_BindsOnceInCall_AndTearsDownOnDisconnect()
    {
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_GlobalVoiceBarTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = tempDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["Spokes_DemoMode"] = "false"
            }).Build();

            var voiceJsMock = new Mock<IVoiceJsInteropService>();
            voiceJsMock.SetupGet(x => x.IsModuleLoaded).Returns(true);
            voiceJsMock.Setup(x => x.ConnectToVoiceAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Microsoft.JSInterop.DotNetObjectReference<GlobalVoiceService>>(),
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<VoicePreJoinContext?>()))
                .ReturnsAsync(true);
            var voiceState = new VoiceStateService();
            var chatState = new ChatStateService();
            var voiceChannelMock = new Mock<VoiceChannelService>();
            var voiceService = new GlobalVoiceService(
                voiceJsMock.Object,
                voiceChannelMock.Object,
                voiceState,
                chatState,
                NullLogger<GlobalVoiceService>.Instance);

            var drawerModule = JSInterop.SetupModule(inv =>
                inv.Arguments.Count > 0 &&
                inv.Arguments[0]?.ToString()?.Contains("GlobalVoiceBar.razor.js", StringComparison.Ordinal) == true);
            drawerModule.Mode = JSRuntimeMode.Loose;
            drawerModule.Setup<bool>("syncVoiceBarPullDrawer", _ => true).SetResult(true);

            var voiceUiStateMock = new Mock<IVoiceUiStateService>();
            voiceUiStateMock.SetupGet(x => x.AvailableMicrophones).Returns([new AudioDevice { DeviceId = "mic-1", Label = "Test Mic" }]);
            voiceUiStateMock.SetupGet(x => x.AvailableSpeakers).Returns([]);
            voiceUiStateMock.SetupGet(x => x.AvailableCameras).Returns([]);

            Services.AddLogging();
            Services.AddSingleton<IConfiguration>(config);
            Services.AddSingleton<EncryptionService>();
            Services.AddSpokesDatabase();
            Services.AddSingleton(voiceService);
            Services.AddSingleton<IVoiceDeviceService>(Mock.Of<IVoiceDeviceService>());
            Services.AddSingleton<IDemoVoiceAdapter>(Mock.Of<IDemoVoiceAdapter>());
            Services.AddSingleton(voiceUiStateMock.Object);
            Services.AddScoped<StartupTimingService>();
            Services.AddScoped<AuthenticationStateProvider>(_ => Mock.Of<AuthenticationStateProvider>());
            Services.AddScoped<UserService>(sp =>
            {
                var db = sp.GetRequiredService<Database>();
                return new UserService(sp.GetRequiredService<AuthenticationStateProvider>(), db.Employees, db.OpenIdAccounts);
            });

            // 1. Initial idle render -> zero inline <style>, zero syncVoiceBarPullDrawer invocations
            var cut = Render<GlobalVoiceBar>();
            Assert.DoesNotContain("<style", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");

            // 2. Connecting state (inCall=false, connecting=true) -> still ZERO syncVoiceBarPullDrawer invocations
            await cut.InvokeAsync(() => voiceService.TryBeginConnecting("voice-ch-1"));
            Assert.Contains("voice-bar-connecting-spinner", cut.Markup);
            Assert.DoesNotContain(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");

            // 3. Connected state (inCall=true) -> mounts #voice-bar-pull-tab and calls syncVoiceBarPullDrawer ONCE
            await cut.InvokeAsync(async () =>
            {
                await voiceService.ConnectAsync(new VoiceConnectionOptions
                {
                    Token = "tok",
                    Url = "wss://livekit.test",
                    ChannelId = "voice-ch-1",
                    UserId = "u1",
                    DeviceId = "dev1",
                    DeviceName = "PC"
                });
            });
            Assert.NotNull(cut.Find("#voice-bar-pull-tab"));
            Assert.Single(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");

            // 4. Disconnect -> unmounts #voice-bar-pull-tab and calls syncVoiceBarPullDrawer ONCE more to clean up
            await cut.InvokeAsync(async () => await voiceService.DisconnectAsync());
            Assert.Empty(cut.FindAll("#voice-bar-pull-tab"));
            Assert.Equal(2, drawerModule.Invocations.Count(i => i.Identifier == "syncVoiceBarPullDrawer"));
        }
        finally
        {
            try { if (Directory.Exists(tempDataDir)) Directory.Delete(tempDataDir, recursive: true); } catch { }
        }
    }
}
