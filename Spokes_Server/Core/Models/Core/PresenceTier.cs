namespace Spokes_Server.Core.Models.Core;

/// <summary>
/// Determines which devices should receive push notifications based on user presence.
/// </summary>
public enum PresenceTier
{
    /// <summary>User is actively focused. No push notifications sent, only local UI updates.</summary>
    None,

    /// <summary>Send push only to mobile devices (e.g. for delayed notifications)</summary>
    MobileOnly,

    /// <summary>User has desktop tab open (unfocused) or was recently seen on desktop — desktop push only</summary>
    DesktopOnly,

    /// <summary>User is not recently seen on desktop — notify all devices including mobile</summary>
    All
}
