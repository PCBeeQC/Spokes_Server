namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Data;

public class ClientPo : IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PoNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string FilePath { get; set; } = string.Empty; // Path or URL to the document
    public string FileName { get; set; } = string.Empty;
    public DateTime? DateReceived { get; set; } = DateTime.Today;

    // Optional: MimeType if we want to show icons correctly
    public string MimeType { get; set; } = string.Empty;

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(FilePath)) yield return FilePath;
    }
}
