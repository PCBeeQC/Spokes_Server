using Spokes_Server.Components.UI;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class OptimisticRouteTransitionGuardTests
{
    [Fact]
    public void PreAckParameterUpdateDuringSelfNavigation_IsIgnoredUntilBrowserEchoesTargetChannel()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat?client=mobile");

        // User taps Voice Room from /chat and triggers an optimistic self-navigation.
        Assert.True(guard.TryBeginSelfNavigation(
            "https://spokes.local/chat?client=mobile",
            "https://spokes.local/chat/chan_voice?client=mobile",
            "chan_voice"));
        Assert.True(guard.HasPendingNavigation);
        Assert.Equal("chan_voice", guard.LatestTargetRouteKey);

        // An intermediate parameter update for the pre-navigation route arrives before OnLocationChanged returns.
        Assert.True(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat?client=mobile",
            currentUrlRouteKey: null,
            incomingRouteKey: null));
        Assert.True(guard.HasPendingNavigation);

        // Browser OnLocationChanged finally arrives for /chat/chan_voice?client=mobile.
        Assert.False(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat/chan_voice?client=mobile",
            currentUrlRouteKey: "chan_voice",
            incomingRouteKey: "chan_voice"));
        Assert.False(guard.HasPendingNavigation);
        Assert.Null(guard.LatestTargetRouteKey);
    }

    [Fact]
    public void PreAckParameterUpdateDuringGoBack_IsIgnoredWithoutReopeningChannel()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat/chan_voice?client=mobile");

        Assert.True(guard.TryBeginSelfNavigation(
            "https://spokes.local/chat/chan_voice?client=mobile",
            "https://spokes.local/chat?client=mobile",
            targetRouteKey: null));

        // A parameter update carrying the prior channel arrives before OnLocationChanged(/chat) returns.
        Assert.True(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat/chan_voice?client=mobile",
            currentUrlRouteKey: "chan_voice",
            incomingRouteKey: "chan_voice"));

        // OnLocationChanged(/chat) arrives and clears the pending self-navigation.
        Assert.False(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat?client=mobile",
            currentUrlRouteKey: null,
            incomingRouteKey: null));
        Assert.False(guard.HasPendingNavigation);
    }

    [Fact]
    public void RapidGoBackThenSelectSameVoiceChannel_IgnoresIntermediateListEchoAndKeepsVoiceChannel()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat/chan_voice?client=mobile");

        // User taps Back, then immediately taps chan_voice before /chat OnLocationChanged returns over WAN.
        Assert.True(guard.TryBeginSelfNavigation(
            "https://spokes.local/chat/chan_voice?client=mobile",
            "https://spokes.local/chat?client=mobile",
            targetRouteKey: null));
        Assert.True(guard.TryBeginSelfNavigation(
            "https://spokes.local/chat/chan_voice?client=mobile",
            "https://spokes.local/chat/chan_voice?client=mobile",
            targetRouteKey: "chan_voice"));

        // Intermediate /chat LocationChanged arrives first -> must be ignored so chan_voice isn't wiped!
        Assert.True(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat?client=mobile",
            currentUrlRouteKey: null,
            incomingRouteKey: null));
        // Idempotent second call from HandleLocationChanged for the same URI must also ignore.
        Assert.True(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat?client=mobile",
            currentUrlRouteKey: null,
            incomingRouteKey: null));
        Assert.True(guard.HasPendingNavigation);
        Assert.Equal("chan_voice", guard.LatestTargetRouteKey);

        // Final /chat/chan_voice LocationChanged arrives -> queue drains cleanly.
        Assert.False(guard.ShouldIgnoreStaleUpdate(
            currentNavUri: "https://spokes.local/chat/chan_voice?client=mobile",
            currentUrlRouteKey: "chan_voice",
            incomingRouteKey: "chan_voice"));
        Assert.False(guard.HasPendingNavigation);
    }

    [Fact]
    public void RapidChannelSwitch_IgnoresSupersededIntermediateChannelEcho()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat");

        Assert.True(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_a", "chan_a"));
        Assert.True(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_b", "chan_b"));

        // Superseded chan_a echo arrives first -> ignored.
        Assert.True(guard.ShouldIgnoreStaleUpdate("https://spokes.local/chat/chan_a", "chan_a", "chan_a"));
        Assert.True(guard.HasPendingNavigation);

        // Target chan_b echo arrives -> settled.
        Assert.False(guard.ShouldIgnoreStaleUpdate("https://spokes.local/chat/chan_b", "chan_b", "chan_b"));
        Assert.False(guard.HasPendingNavigation);
    }

    [Fact]
    public void ExternalNavigationDuringInFlightSelfNav_ClearsQueueAndIsNotIgnored()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat");

        Assert.True(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_a", "chan_a"));

        // Push notification or external Nav.NavigateTo redirects to chan_push before chan_a echoes.
        Assert.False(guard.ShouldIgnoreStaleUpdate("https://spokes.local/chat/chan_push", "chan_push", "chan_push"));
        Assert.False(guard.HasPendingNavigation);
        Assert.Null(guard.LatestTargetRouteKey);
    }

    [Fact]
    public void BrowserHistoryBackWhenIdle_IsNeverIgnored()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat/chan_voice");

        Assert.False(guard.ShouldIgnoreStaleUpdate("https://spokes.local/chat", null, null));
        Assert.False(guard.HasPendingNavigation);
    }

    [Fact]
    public void DuplicateSelfNavigationToSameTargetUri_IsDeduplicated()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat");

        // Already at /chat -> returns false without polluting the queue.
        Assert.False(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat", null));
        Assert.False(guard.HasPendingNavigation);

        // Enqueue chan_voice once; a duplicate back-to-back call for chan_voice is deduplicated.
        Assert.True(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_voice", "chan_voice"));
        Assert.False(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_voice", "chan_voice"));

        // Single echo clears the queue completely.
        Assert.False(guard.ShouldIgnoreStaleUpdate("https://spokes.local/chat/chan_voice", "chan_voice", "chan_voice"));
        Assert.False(guard.HasPendingNavigation);
    }

    [Fact]
    public void Reset_ClearsPendingExpectationsAndUpdatesObservedUri()
    {
        var guard = new OptimisticRouteTransitionGuard();
        guard.SyncInitialUri("https://spokes.local/chat");
        Assert.True(guard.TryBeginSelfNavigation("https://spokes.local/chat", "https://spokes.local/chat/chan_voice", "chan_voice"));

        guard.Reset("https://spokes.local/albums");
        Assert.False(guard.HasPendingNavigation);
        Assert.Null(guard.LatestTargetRouteKey);
    }
}
