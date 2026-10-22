namespace Spokes_Server.Core.Configuration;

public class EmailServerSettings
{
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapSsl { get; set; } = true;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } // StartTLS usually
}
