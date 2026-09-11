namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using System;




public class ClientPO
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PoNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string FilePath { get; set; } = string.Empty; // Path or URL to the document
    public DateTime? DateReceived { get; set; } = DateTime.Today;

    // Optional: MimeType if we want to show icons correctly
    public string MimeType { get; set; } = string.Empty;
}



