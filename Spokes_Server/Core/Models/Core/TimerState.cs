namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class TimerState
{
    public string EmployeeId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string? WorkTypeId { get; set; }
    public string? SubTaskId { get; set; }
    public DateTime? StartedAt { get; set; }
    public bool IsRunning { get; set; }
    public string Note { get; set; } = string.Empty;
}



