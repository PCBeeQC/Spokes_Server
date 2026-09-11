namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;




public class CalendarEvent : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (CalendarEvent)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty; // Optional link to a project

    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsAllDay { get; set; }

    public string Location { get; set; } = string.Empty;

    public string ExternalParticipant { get; set; } = string.Empty;

    // Future-proofing
    public List<string> Attendees { get; set; } = new();
    public List<string> InvitedTeamIds { get; set; } = new();
    public string Category { get; set; } = string.Empty; // Work, Meeting, Holiday, etc.

    public bool IsCompanyWide { get; set; }
}



