namespace Spokes_Server.Core.Configuration;

using System.Collections.Generic;

public enum TextModerationAction
{
    Block,
    Sanitize
}

public class ModerationSettings
{
    public bool EnableTextFilter { get; set; }
    public TextModerationAction TextAction { get; set; } = TextModerationAction.Block;
    public List<string> BlockedWords { get; set; } = [];
}
