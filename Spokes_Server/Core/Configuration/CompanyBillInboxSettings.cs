namespace Spokes_Server.Core.Configuration;

/// <summary>
/// Settings for the company-wide catch-all billing mailbox (e.g. factures@company.com).
/// </summary>
public class CompanyBillInboxSettings
{
    /// <summary>
    /// Whether automated background ingestion is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Email address of the company billing mailbox (e.g. factures@poly-robotics.com).
    /// </summary>
    public string EmailAddress { get; set; } = string.Empty;

    /// <summary>
    /// Display name when sending from this mailbox.
    /// </summary>
    public string DisplayName { get; set; } = "Facturation";

    // --- IMAP (Receiving) ---
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public bool ImapSsl { get; set; } = true;
    public string? ImapUsername { get; set; }
    public string EncryptedImapPassword { get; set; } = string.Empty;
    public string FolderToMonitor { get; set; } = "INBOX";
    public int CheckIntervalMinutes { get; set; } = 15;

    // --- SMTP (Sending / Replying) ---
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } // StartTLS usually
    public string? SmtpUsername { get; set; }
    public string EncryptedSmtpPassword { get; set; } = string.Empty;

    // --- Ingestion & Matching Rules ---
    public string DefaultCurrency { get; set; } = "CAD";
    public int DuplicateDateWindowDays { get; set; } = 20;
    public decimal PoMatchToleranceAmount { get; set; } = 1.00m;
    public bool AutoMatchRecurringExpenses { get; set; } = true;
    public bool AllowDraftWithoutPdfForRecurring { get; set; } = true;

    // --- Multilingual Keyword Classification Lists ---
    public List<string> StatementKeywords { get; set; } = [
        "statement of account", "account statement", "monthly statement", "billing statement",
        "état de compte", "etat de compte", "sommaire de compte", "relevé de compte", "releve de compte",
        "kontoauszug", "rechnungsauszug", "extracto de cuenta", "estado de cuenta"
    ];

    public List<string> ReceiptKeywords { get; set; } = [
        "payment receipt", "receipt for your payment", "payment confirmation", "we received your payment",
        "reçu de paiement", "recu de paiement", "confirmation de paiement", "paiement reçu",
        "zahlungsbestätigung", "comprobante de pago"
    ];

    public List<string> CreditNoteKeywords { get; set; } = [
        "credit note", "credit memo", "credit memo invoice", "note de crédit", "note de credit",
        "avoir", "gutschrift", "nota de crédito", "nota de credito"
    ];

    // --- Mailbox Hygiene & Notifications ---
    public List<string> IgnoredSenderDomains { get; set; } = [];
    public string ProcessedFolderName { get; set; } = "Processed_Bills";
    public string DeletedFolderName { get; set; } = "SPOKES_DELETED";
    public bool NotifyOnNewBills { get; set; } = true;
    public bool EnableDebugLog { get; set; } = false;

    // --- Status Tracking ---
    public DateTime? LastCheckedAt { get; set; }
    public string? LastCheckStatus { get; set; }
    public int LastIngestedCount { get; set; }
}
