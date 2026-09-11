namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class ClientInfo
{
    public string BusinessName { get; set; } = string.Empty;
    public string ContactPersonPrefix { get; set; } = string.Empty;
    public string ContactPersonName { get; set; } = string.Empty;
    public string ContactPersonTitle { get; set; } = string.Empty;
    public string ContactPersonEmail { get; set; } = string.Empty;
    public string ContactPersonPhone { get; set; } = string.Empty;
    public string BusinessAdressNumber { get; set; } = string.Empty;
    public string BusinessAdressStreet { get; set; } = string.Empty;
    public string BusinessAdressCity { get; set; } = string.Empty;
    public string BusinessAdressState { get; set; } = string.Empty;
    public string BusinessAdressCountry { get; set; } = string.Empty;
    public string BusinessAdressZip { get; set; } = string.Empty;
}



