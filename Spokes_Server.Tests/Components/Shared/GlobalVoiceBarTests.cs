using System.Diagnostics;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using MudBlazor.Services;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Shared;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Components.Shared;

public class GlobalVoiceBarTests : BunitContext
{
    private readonly List<string> _tempDirs = new();

    public GlobalVoiceBarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        base.Dispose(disposing);
    }

    private (GlobalVoiceService VoiceService, VoiceDeviceService DeviceService, List<AudioDevice> Mics, List<AudioDevice> Speakers, BunitJSModuleInterop DrawerModule) SetupVoiceBarServices(
        bool demoMode = false)
    {
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_GlobalVoiceBarTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        _tempDirs.Add(tempDataDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = tempDataDir,
            ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = demoMode ? "true" : "false"
        }).Build();

        var voiceJsMock = new Mock<IVoiceJsInteropService>();
        voiceJsMock.SetupGet(x => x.IsModuleLoaded).Returns(true);
        voiceJsMock.Setup(x => x.ConnectToVoiceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DotNetObjectReference<GlobalVoiceService>>(),
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<VoicePreJoinContext?>()))
            .ReturnsAsync(true);

        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var voiceChannelMock = new Mock<VoiceChannelService>();
        var deviceService = new VoiceDeviceService(voiceJsMock.Object, NullLogger<VoiceDeviceService>.Instance);
        var voiceService = new GlobalVoiceService(
            voiceJsMock.Object,
            voiceChannelMock.Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance,
            deviceService);

        var mics = new List<AudioDevice>();
        var speakers = new List<AudioDevice>();
        var uiStateMock = new Mock<IVoiceUiStateService>();
        uiStateMock.SetupGet(x => x.AvailableMicrophones).Returns(() => mics);
        uiStateMock.SetupGet(x => x.AvailableSpeakers).Returns(() => speakers);
        uiStateMock.SetupGet(x => x.AvailableCameras).Returns([]);
        uiStateMock.SetupProperty(x => x.ActiveMicrophoneDeviceId, "default");
        uiStateMock.SetupProperty(x => x.ActiveSpeakerDeviceId, "default");
        uiStateMock.SetupProperty(x => x.ActiveCameraDeviceId, "");
        uiStateMock.SetupProperty(x => x.MaxVideoSendQuality, "Auto");
        uiStateMock.SetupProperty(x => x.MaxVideoSendFps, 30);
        uiStateMock.SetupProperty(x => x.MaxScreenShareQuality, "Auto");
        uiStateMock.SetupProperty(x => x.MaxScreenShareFps, 30);
        uiStateMock.SetupProperty(x => x.ScreenShareMode, "motion");

        var drawerModule = JSInterop.SetupModule(inv =>
            inv.Arguments.Count > 0 &&
            inv.Arguments[0]?.ToString()?.Contains("GlobalVoiceBar.razor.js", StringComparison.Ordinal) == true);
        drawerModule.Mode = JSRuntimeMode.Loose;

        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();
        Services.AddSingleton(voiceService);
        Services.AddSingleton<IVoiceDeviceService>(deviceService);
        Services.AddSingleton<IDemoVoiceAdapter>(Mock.Of<IDemoVoiceAdapter>());
        Services.AddSingleton<IVoiceUiStateService>(uiStateMock.Object);
        Services.AddScoped<UserCircuitContext>();
        Services.AddScoped<StartupTimingService>();
        Services.AddScoped<AuthenticationStateProvider>(_ => Mock.Of<AuthenticationStateProvider>());
        Services.AddScoped<UserService>(sp =>
        {
            var db = sp.GetRequiredService<Database>();
            return new UserService(sp.GetRequiredService<AuthenticationStateProvider>(), db.Employees, db.OpenIdAccounts);
        });

        return (voiceService, deviceService, mics, speakers, drawerModule);
    }

    [Fact]
    public async Task GlobalVoiceBar_DemoMode_NeverRendersPullTab_EvenWhenDevicesAndVideoPresent()
    {
        var (voiceService, deviceService, mics, speakers, drawerModule) = SetupVoiceBarServices(demoMode: true);
        drawerModule.Setup<bool>("syncVoiceBarPullDrawer", _ => true).SetResult(true);
        mics.Add(new AudioDevice { DeviceId = "mic-1", Label = "USB Mic" });
        speakers.Add(new AudioDevice { DeviceId = "spk-1", Label = "Headset" });

        var cut = Render<GlobalVoiceBar>();
        await cut.InvokeAsync(async () =>
        {
            await voiceService.ConnectAsync(new VoiceConnectionOptions
            {
                Token = "tok",
                Url = "wss://livekit.test",
                ChannelId = "demo-ch",
                UserId = "u1",
                DeviceId = "dev1",
                DeviceName = "PC"
            });
            deviceService.HandleAudioDevicesUpdated(mics, "mic-1", speakers, "spk-1");
        });

        Assert.NotEmpty(cut.FindAll(".voice-call-panel"));
        Assert.Empty(cut.FindAll("#voice-bar-pull-tab"));
        Assert.DoesNotContain(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");
    }

    [Fact]
    public async Task GlobalVoiceBar_HidesChevronWhenNoDrawerControls_AndExpandsRealControlsOnceDevicesArrive()
    {
        var (voiceService, deviceService, mics, speakers, drawerModule) = SetupVoiceBarServices(demoMode: false);
        drawerModule.Setup<bool>("syncVoiceBarPullDrawer", _ => true).SetResult(true);

        var cut = Render<GlobalVoiceBar>();

        // 1. Connect without any enumerated devices, video, or screen share -> chevron must NOT render
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

        Assert.NotEmpty(cut.FindAll(".voice-call-panel"));
        Assert.Empty(cut.FindAll("#voice-bar-pull-tab"));
        Assert.DoesNotContain(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");

        // 2. Audio devices arrive -> chevron renders, syncVoiceBarPullDrawer binds, and expanding renders real selects
        mics.Add(new AudioDevice { DeviceId = "default", Label = "System Default" });
        speakers.Add(new AudioDevice { DeviceId = "default", Label = "System Default" });
        await cut.InvokeAsync(() => deviceService.HandleAudioDevicesUpdated(mics, "default", speakers, "default"));

        Assert.NotNull(cut.Find("#voice-bar-pull-tab"));
        Assert.Single(drawerModule.Invocations, i => i.Identifier == "syncVoiceBarPullDrawer");

        await cut.InvokeAsync(() => cut.Instance.OnDrawerExpandedChanged(true));
        Assert.Equal("true", cut.Find("#voice-bar-pull-tab").GetAttribute("aria-expanded"));
        Assert.NotEmpty(cut.FindAll(".voice-drawer-select"));
        Assert.Equal(2, drawerModule.Invocations.Count(i => i.Identifier == "syncVoiceBarPullDrawer"));
    }

    [Fact]
    public async Task GlobalVoiceBar_RetriesSyncWhenJsReturnsFalse_AndReSyncsOnChannelSwitch()
    {
        var (voiceService, deviceService, mics, speakers, drawerModule) = SetupVoiceBarServices(demoMode: false);
        var syncHandler = drawerModule.Setup<bool>("syncVoiceBarPullDrawer", _ => true);
        syncHandler.SetResult(false);

        mics.Add(new AudioDevice { DeviceId = "default", Label = "System Default" });
        speakers.Add(new AudioDevice { DeviceId = "default", Label = "System Default" });

        var cut = Render<GlobalVoiceBar>();

        // 1. Connect to channel 1; first JS sync returns false (simulating transient DOM miss) -> must NOT latch
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

        // 2. Subsequent render (e.g. toggling mute) must retry syncVoiceBarPullDrawer because first attempt returned false
        syncHandler.SetResult(true);
        await cut.InvokeAsync(async () => await voiceService.ToggleMuteAsync());
        Assert.Equal(2, drawerModule.Invocations.Count(i => i.Identifier == "syncVoiceBarPullDrawer"));

        // 3. Once bound (returned true), another mute toggle must NOT emit redundant syncVoiceBarPullDrawer calls
        await cut.InvokeAsync(async () => await voiceService.ToggleMuteAsync());
        Assert.Equal(2, drawerModule.Invocations.Count(i => i.Identifier == "syncVoiceBarPullDrawer"));

        // 4. Switching directly to channel 2 must re-sync pull drawer for the new channel
        await cut.InvokeAsync(async () =>
        {
            await voiceService.ConnectAsync(new VoiceConnectionOptions
            {
                Token = "tok-2",
                Url = "wss://livekit.test",
                ChannelId = "voice-ch-2",
                UserId = "u1",
                DeviceId = "dev1",
                DeviceName = "PC"
            });
        });
        Assert.True(drawerModule.Invocations.Count(i => i.Identifier == "syncVoiceBarPullDrawer") >= 3);
    }

    [Fact]
    public async Task GlobalVoiceBarJs_ClickKeyboardHeightSyncAndDomRebind_WorkEndToEnd()
    {
        var serverRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Spokes_Server"));
        var jsSourcePath = Path.Combine(serverRoot, "Components", "Shared", "GlobalVoiceBar.razor.js");
        Assert.True(File.Exists(jsSourcePath), $"Missing {jsSourcePath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"spokes_voicebar_js_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);

        await File.WriteAllTextAsync(Path.Combine(tempDir, "GlobalVoiceBar.razor.js"), await File.ReadAllTextAsync(jsSourcePath, Encoding.UTF8), Encoding.UTF8);

        const string runnerScript = """
            import assert from 'node:assert/strict';

            function createElement(id) {
                const listeners = new Map();
                const classes = new Set();
                let capturedPointer = null;
                let rectHeight = 0;
                const el = {
                    id,
                    scrollHeight: 60,
                    offsetHeight: 60,
                    style: {
                        height: '0px',
                        transition: '',
                        transform: '',
                        opacity: ''
                    },
                    classList: {
                        toggle(name, force) {
                            if (force === undefined) {
                                if (classes.has(name)) classes.delete(name);
                                else classes.add(name);
                            } else if (force) {
                                classes.add(name);
                            } else {
                                classes.delete(name);
                            }
                        },
                        contains(name) { return classes.has(name); }
                    },
                    addEventListener(type, fn) {
                        if (!listeners.has(type)) listeners.set(type, new Set());
                        listeners.get(type).add(fn);
                    },
                    removeEventListener(type, fn) {
                        listeners.get(type)?.delete(fn);
                    },
                    dispatch(type, event = {}) {
                        const ev = { preventDefault() {}, ...event };
                        for (const fn of [...(listeners.get(type) || [])]) {
                            fn(ev);
                        }
                    },
                    setPointerCapture(id) { capturedPointer = id; },
                    hasPointerCapture(id) { return capturedPointer === id; },
                    releasePointerCapture(id) { if (capturedPointer === id) capturedPointer = null; },
                    getBoundingClientRect() {
                        const parsed = parseFloat(el.style.height);
                        return { height: Number.isFinite(parsed) ? parsed : rectHeight };
                    },
                    listenerCount(type) {
                        return listeners.get(type)?.size || 0;
                    }
                };
                return el;
            }

            let domMap = new Map();
            globalThis.document = {
                getElementById(id) { return domMap.get(id) || null; }
            };

            const mod = await import('./GlobalVoiceBar.razor.js');
            const dotNetCalls = [];
            const dotNetRef = {
                async invokeMethodAsync(method, arg) {
                    dotNetCalls.push({ method, arg });
                }
            };

            // 1. Missing DOM elements -> returns false
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), false);

            // 2. Mount DOM elements -> returns true and binds listeners
            let w = createElement('w');
            let c = createElement('c');
            let t = createElement('t');
            let i = createElement('i');
            domMap = new Map([['w', w], ['c', c], ['t', t], ['i', i]]);

            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), true);
            assert.equal(t.listenerCount('pointerdown'), 1);
            assert.equal(t.listenerCount('click'), 1);
            assert.equal(t.listenerCount('keydown'), 1);

            // 3. Pointer click opens drawer ONCE (subsequent synthetic browser click must NOT double-toggle)
            t.dispatch('pointerdown', { button: 0, pointerId: 1, clientY: 50 });
            assert.equal(i.style.transform, 'rotate(0deg)', 'Icon rotation must stay locked on pointerdown');
            // Simulate Blazor mounting taller content during press
            c.scrollHeight = 140;
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, true), true);
            t.dispatch('pointerup', { button: 0, pointerId: 1, clientY: 50 });
            t.dispatch('click', {});

            assert.equal(w.classList.contains('is-expanded'), true);
            assert.equal(w.style.height, '140px');
            assert.equal(i.style.transform, 'rotate(180deg)');

            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.style.height, 'auto');
            assert.equal(dotNetCalls.at(-1)?.arg, true);

            // 4. Second pointer click collapses drawer cleanly
            t.dispatch('pointerdown', { button: 0, pointerId: 2, clientY: 190 });
            t.dispatch('pointerup', { button: 0, pointerId: 2, clientY: 190 });
            t.dispatch('click', {});

            assert.equal(w.classList.contains('is-expanded'), false);
            assert.equal(w.style.height, '0px');
            assert.equal(i.style.transform, 'rotate(0deg)');

            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.style.height, '0px');
            assert.equal(dotNetCalls.at(-1)?.arg, false);

            // 5. Wait past dedupe window -> keyboard Enter toggles drawer open
            await new Promise(r => setTimeout(r, 420));
            t.dispatch('keydown', { key: 'Enter' });
            assert.equal(w.classList.contains('is-expanded'), true);
            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.style.height, 'auto');

            // Collapse via keyboard so we can verify mobile touch tap with natural thumb jitter
            t.dispatch('keydown', { key: 'Enter' });
            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.classList.contains('is-expanded'), false);
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), true);

            // 5b. Touch tap with 10px thumb jitter (within 12px touch slop) must toggle OPEN instead of snapping shut
            const callsBeforeTouchTap = dotNetCalls.length;
            t.dispatch('pointerdown', { button: 0, pointerId: 3, pointerType: 'touch', clientY: 50 });
            t.dispatch('pointermove', { pointerId: 3, pointerType: 'touch', clientY: 60 });
            t.dispatch('pointerup', { button: 0, pointerId: 3, pointerType: 'touch', clientY: 60 });
            assert.equal(w.classList.contains('is-expanded'), true, '10px touch tap jitter must toggle drawer open');
            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.style.height, 'auto');
            assert.equal(dotNetCalls.length - callsBeforeTouchTap, 1, 'Tap-to-open must deduplicate OnDrawerExpandedChanged(true)');
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, true), true);

            // 5c. Aborted drag on WAN: stale syncFromServer(true) from pointerdown must NOT pop drawer back open
            t.dispatch('keydown', { key: 'Enter' });
            await new Promise(r => setTimeout(r, 260));
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), true);
            t.dispatch('pointerdown', { button: 0, pointerId: 4, pointerType: 'touch', clientY: 50 });
            // Slow drag 20px over 100ms (past 12px touch slop, low velocity < 0.35px/ms, height < 40% of 140px) then release to snap back closed
            await new Promise(r => setTimeout(r, 100));
            t.dispatch('pointermove', { pointerId: 4, pointerType: 'touch', clientY: 70 });
            t.dispatch('pointerup', { button: 0, pointerId: 4, pointerType: 'touch', clientY: 70 });
            await new Promise(r => setTimeout(r, 260));
            assert.equal(w.classList.contains('is-expanded'), false);
            // Late pointerdown pre-mount echo arrives after animation ended, before collapse ack arrives
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, true), true);
            assert.equal(w.classList.contains('is-expanded'), false, 'Stale pointerdown echo must not re-open aborted drag');
            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), true);

            // 6. Replacing DOM elements -> destroys old controller listeners and binds new element
            const oldT = t;
            w = createElement('w');
            c = createElement('c');
            t = createElement('t');
            i = createElement('i');
            domMap = new Map([['w', w], ['c', c], ['t', t], ['i', i]]);

            assert.equal(mod.syncVoiceBarPullDrawer('w', 'c', 't', 'i', dotNetRef, false), true);
            assert.equal(oldT.listenerCount('pointerdown'), 0, 'Old pull-tab listeners must be cleaned up');
            assert.equal(t.listenerCount('pointerdown'), 1, 'New pull-tab must be bound');

            mod.disposeVoiceBarPullDrawer();
            assert.equal(t.listenerCount('pointerdown'), 0);
            console.log('VOICE_BAR_PULL_DRAWER_JS_OK');
            """;

        var runnerPath = Path.Combine(tempDir, "runner.mjs");
        await File.WriteAllTextAsync(runnerPath, runnerScript, Encoding.UTF8);

        var psi = new ProcessStartInfo("node", runnerPath)
        {
            WorkingDirectory = tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0, $"Node runner failed ({proc.ExitCode}):\nSTDOUT: {stdout}\nSTDERR: {stderr}");
        Assert.Contains("VOICE_BAR_PULL_DRAWER_JS_OK", stdout);
    }

    [Fact]
    public async Task GlobalVoiceBar_MobileSwitchCameraButton_RendersOnlyWhenVideoActiveAndFlipsCamera()
    {
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_GlobalVoiceBarTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        _tempDirs.Add(tempDataDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = tempDataDir,
            ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = "false"
        }).Build();

        var voiceJsMock = new Mock<IVoiceJsInteropService>();
        voiceJsMock.SetupGet(x => x.IsModuleLoaded).Returns(true);
        voiceJsMock.Setup(x => x.ConnectToVoiceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DotNetObjectReference<GlobalVoiceService>>(),
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<VoicePreJoinContext?>()))
            .ReturnsAsync(true);
        voiceJsMock.Setup(x => x.SetCameraEnabledAsync(true, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new CameraInitResult
            {
                Cameras =
                [
                    new CameraDevice { DeviceId = "cam-front", Label = "Front Camera" },
                    new CameraDevice { DeviceId = "cam-back", Label = "Back Camera" }
                ],
                ActiveCameraDeviceId = "cam-front"
            });
        voiceJsMock.Setup(x => x.SwitchActiveCameraAsync(null))
            .ReturnsAsync("cam-back");

        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var voiceChannelMock = new Mock<VoiceChannelService>();
        var deviceService = new VoiceDeviceService(voiceJsMock.Object, NullLogger<VoiceDeviceService>.Instance);
        var voiceService = new GlobalVoiceService(
            voiceJsMock.Object,
            voiceChannelMock.Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance,
            deviceService);

        var uiStateMock = new Mock<IVoiceUiStateService>();
        uiStateMock.SetupGet(x => x.AvailableMicrophones).Returns([]);
        uiStateMock.SetupGet(x => x.AvailableSpeakers).Returns([]);
        uiStateMock.SetupGet(x => x.AvailableCameras).Returns(
        [
            new CameraDevice { DeviceId = "cam-front", Label = "Front Camera" },
            new CameraDevice { DeviceId = "cam-back", Label = "Back Camera" }
        ]);
        uiStateMock.SetupProperty(x => x.ActiveCameraDeviceId, "cam-front");
        uiStateMock.SetupProperty(x => x.MaxVideoSendQuality, "Auto");
        uiStateMock.SetupProperty(x => x.MaxVideoSendFps, 30);

        var circuitContext = new UserCircuitContext { IsCapacitorApp = false, IsMobileViewport = false };
        var drawerModule = JSInterop.SetupModule(inv =>
            inv.Arguments.Count > 0 &&
            inv.Arguments[0]?.ToString()?.Contains("GlobalVoiceBar.razor.js", StringComparison.Ordinal) == true);
        drawerModule.Mode = JSRuntimeMode.Loose;
        drawerModule.Setup<bool>("syncVoiceBarPullDrawer", _ => true).SetResult(true);

        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton<EncryptionService>();
        Services.AddSpokesDatabase();
        Services.AddSingleton(voiceService);
        Services.AddSingleton<IVoiceDeviceService>(deviceService);
        Services.AddSingleton<IDemoVoiceAdapter>(Mock.Of<IDemoVoiceAdapter>());
        Services.AddSingleton<IVoiceUiStateService>(uiStateMock.Object);
        Services.AddSingleton(circuitContext);
        Services.AddScoped<StartupTimingService>();
        Services.AddScoped<AuthenticationStateProvider>(_ => Mock.Of<AuthenticationStateProvider>());
        Services.AddScoped<UserService>(sp =>
        {
            var db = sp.GetRequiredService<Database>();
            return new UserService(sp.GetRequiredService<AuthenticationStateProvider>(), db.Employees, db.OpenIdAccounts);
        });

        var cut = Render<GlobalVoiceBar>();
        await cut.InvokeAsync(async () =>
        {
            await voiceService.ConnectAsync(new VoiceConnectionOptions
            {
                ChannelId = "vc-1",
                UserId = "u-1",
                Token = "tok",
                Url = "wss://lk.test",
                DeviceId = "dev-1",
                DeviceName = "Phone"
            });
        });

        // Desktop with video off -> no Switch Camera button
        Assert.Empty(cut.FindAll("button[aria-label='Switch Camera']"));

        // Enable video on desktop -> still no Switch Camera button
        await cut.InvokeAsync(() => voiceService.ToggleVideoAsync("Auto", 30, ""));
        Assert.Empty(cut.FindAll("button[aria-label='Switch Camera']"));

        // Switch to mobile device while video is active -> button appears and flips camera on click
        await cut.InvokeAsync(() => circuitContext.DeviceType = Spokes_Server.Core.Models.Core.DeviceType.Mobile);
        var switchBtn = cut.Find("button[aria-label='Switch Camera']");
        await cut.InvokeAsync(() => switchBtn.Click());
        voiceJsMock.Verify(x => x.SwitchActiveCameraAsync(null), Times.Once);

        // Turn video off on mobile -> button disappears
        await cut.InvokeAsync(() => voiceService.ToggleVideoAsync("Auto", 30, ""));
        Assert.Empty(cut.FindAll("button[aria-label='Switch Camera']"));
    }
}
