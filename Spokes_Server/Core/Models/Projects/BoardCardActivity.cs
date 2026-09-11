namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class BoardCardActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CardId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty; // Employee Id (or System)
    public string Action { get; set; } = string.Empty; // e.g. "updated", "commented"
    public string Details { get; set; } = string.Empty; // e.g. "Status to Doing"
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}



