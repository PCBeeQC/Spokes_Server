namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class CommissionBeneficiary
{
    public string Name { get; set; } = string.Empty; // Employee Name or External Entity Name
    public string Type { get; set; } = "Internal"; // "Internal" or "External"
    public string Role { get; set; } = "Sales"; // e.g. "Account Executive", "Project Manager"
    public string CommissionBasis { get; set; } = Spokes_Server.Core.Models.Accounting.CommissionBasisTypes.Revenue; // "Revenue" or "Margin"
    public string? EmployeeId { get; set; } // Optional, links to Employee
    public decimal Percentage { get; set; } // The cut they get (0-100)

    // Payment Tracking
    public DateTime? DatePaid { get; set; }
    public string? PaymentReference { get; set; }
}



