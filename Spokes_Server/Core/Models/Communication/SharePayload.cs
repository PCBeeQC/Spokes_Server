using System.Collections.Generic;

namespace Spokes_Server.Core.Models.Communication;

public class SharePayload
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Url { get; set; }
    public List<SharedFile>? Files { get; set; }
    public string? TargetChannelId { get; set; }
    public string? TargetServerUrl { get; set; }

    /// <summary>
    /// Compat: @capgo/capacitor-share-target sends "texts" (plural array).
    /// Merged into Text/Url by OnShareReceived if present.
    /// </summary>
    public List<string>? Texts { get; set; }
}

public class SharedFile
{
    public string? Name { get; set; }
    public string? MimeType { get; set; }
    public string? Uri { get; set; }
}
