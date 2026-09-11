namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class AllocationResult
{
    public string EmployeeId { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public string Note { get; set; } = string.Empty;
}



