namespace Spokes_Server.Components.Shared;

/// <summary>
/// Visual arrangement of avatars inside <see cref="EmployeeAvatarGroup"/>.
/// </summary>
public enum AvatarGroupLayout
{
    /// <summary>Overlapping circular facepile with subtle surface cutout rings.</summary>
    Stacked,

    /// <summary>Spaced horizontal row without avatar overlap.</summary>
    Inline
}

/// <summary>
/// Overflow indicator style shown when the number of employees exceeds <see cref="EmployeeAvatarGroup.Max"/>.
/// </summary>
public enum AvatarOverflowStyle
{
    /// <summary>Circular avatar-sized badge displaying three horizontal dots (⋯).</summary>
    Ellipsis,

    /// <summary>Remaining count (+N) rendered as a circular badge in Stacked mode or compact text in Inline mode.</summary>
    Count
}
