namespace Spokes_Server.Core.Services.Logging;

public class SystemLogEntry
{
    public DateTime TimestampUtc { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}
