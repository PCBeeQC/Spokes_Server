using System;
using System.Collections.Generic;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Tracks self-initiated optimistic Blazor <c>NavigationManager.NavigateTo</c> transitions so
/// superseded intermediate <c>LocationChanged</c> and route-parameter echoes over async WebSocket RTT
/// never overwrite the component's latest optimistic route state before the browser acknowledges the
/// final target URI.
/// </summary>
public sealed class OptimisticRouteTransitionGuard
{
    private readonly Queue<string?> _pendingTargets = new();
    private string? _lastObservedUri;
    private string? _lastTargetAbsoluteUri;

    public bool HasPendingNavigation => _pendingTargets.Count > 0;
    public string? LatestTargetRouteKey { get; private set; }

    /// <summary>
    /// Seeds the initial acknowledged URI before the first optimistic navigation occurs.
    /// </summary>
    public void SyncInitialUri(string? currentUri)
    {
        if (_lastObservedUri == null && !string.IsNullOrEmpty(currentUri))
        {
            _lastObservedUri = currentUri;
        }
    }

    /// <summary>
    /// Records an optimistic self-navigation when <paramref name="targetAbsoluteUri"/> differs
    /// from the effective current URI (either the last queued target or <paramref name="currentNavUri"/>).
    /// Returns <see langword="true"/> when the caller should issue <c>NavigationManager.NavigateTo</c>,
    /// or <see langword="false"/> when that exact URI is already current or queued as the latest target.
    /// </summary>
    public bool TryBeginSelfNavigation(string? currentNavUri, string targetAbsoluteUri, string? targetRouteKey)
    {
        SyncInitialUri(currentNavUri);

        var effectiveCurrentUri = _pendingTargets.Count > 0
            ? _lastTargetAbsoluteUri
            : currentNavUri;

        if (string.Equals(effectiveCurrentUri, targetAbsoluteUri, StringComparison.Ordinal))
        {
            return false;
        }

        _pendingTargets.Enqueue(targetRouteKey);
        _lastTargetAbsoluteUri = targetAbsoluteUri;
        LatestTargetRouteKey = targetRouteKey;
        return true;
    }

    /// <summary>
    /// Clears all pending self-navigation expectations (for example when leaving this page family).
    /// </summary>
    public void Reset(string? currentUri = null)
    {
        _pendingTargets.Clear();
        _lastTargetAbsoluteUri = null;
        LatestTargetRouteKey = null;
        if (!string.IsNullOrEmpty(currentUri))
        {
            _lastObservedUri = currentUri;
        }
    }

    /// <summary>
    /// Idempotently reconciles <paramref name="currentNavUri"/> and returns <see langword="true"/>
    /// when an incoming parameter or location update for <paramref name="incomingRouteKey"/> is a
    /// pre-acknowledgement or superseded intermediate self-navigation echo that must be ignored.
    /// </summary>
    public bool ShouldIgnoreStaleUpdate(string? currentNavUri, string? currentUrlRouteKey, string? incomingRouteKey)
    {
        bool uriChanged = !string.IsNullOrEmpty(currentNavUri) &&
                          !string.Equals(_lastObservedUri, currentNavUri, StringComparison.Ordinal);

        if (uriChanged)
        {
            _lastObservedUri = currentNavUri;

            if (_pendingTargets.Count > 0)
            {
                if (string.Equals(_pendingTargets.Peek(), currentUrlRouteKey, StringComparison.Ordinal))
                {
                    _pendingTargets.Dequeue();
                    if (_pendingTargets.Count == 0)
                    {
                        _lastTargetAbsoluteUri = null;
                        LatestTargetRouteKey = null;
                    }
                }
                else
                {
                    // Browser or external code navigated to a different route (e.g. history.back,
                    // push notification, or nav link): drop stale self-nav expectations.
                    _pendingTargets.Clear();
                    _lastTargetAbsoluteUri = null;
                    LatestTargetRouteKey = null;
                    return false;
                }
            }
        }

        if (_pendingTargets.Count == 0)
        {
            return false;
        }

        return !string.Equals(incomingRouteKey, LatestTargetRouteKey, StringComparison.Ordinal);
    }
}
