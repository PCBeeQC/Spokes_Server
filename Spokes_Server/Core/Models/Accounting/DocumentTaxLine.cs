namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Represents an individual jurisdiction sales tax component calculated or recorded on an accounting document
/// (e.g. TPS 5%, TVQ 9.975%, HST 13%, TVA 20%, or US State Sales Tax).
/// </summary>
public class DocumentTaxLine
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public bool IsRecoverable { get; set; } = true;
    public string AuthorityName { get; set; } = string.Empty;
    public string GlAccountCode { get; set; } = string.Empty;
}
