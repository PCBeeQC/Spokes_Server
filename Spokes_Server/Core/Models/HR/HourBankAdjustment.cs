namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;
using System;

public class HourBankAdjustment : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EmployeeId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Hours { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string CreatedByEmployeeId { get; set; } = string.Empty;
}
