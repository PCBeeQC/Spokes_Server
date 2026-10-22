using System;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class IcsParserTests
{
    [Fact]
    public void ParseFirstEvent_StandardEvent_ExtractsAllFields()
    {
        var ics = @"BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Example Corp.//EN
BEGIN:VEVENT
UID:uid-123@example.com
DTSTAMP:20261001T090000Z
DTSTART:20261001T140000Z
DTEND:20261001T150000Z
SUMMARY:Sprint Planning
LOCATION:Meeting Room A
DESCRIPTION:Review quarterly goals and sprint backlog
ORGANIZER;CN=""Project Lead"":mailto:lead@example.com
END:VEVENT
END:VCALENDAR";

        var evt = IcsParser.ParseFirstEvent(ics);

        Assert.NotNull(evt);
        Assert.Equal("Sprint Planning", evt.Summary);
        Assert.Equal("Meeting Room A", evt.Location);
        Assert.Equal("Review quarterly goals and sprint backlog", evt.Description);
        Assert.Equal(new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc), evt.Start);
        Assert.Equal(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc), evt.End);
        Assert.False(evt.IsAllDay);
        Assert.Equal("Project Lead", evt.Organizer);
    }

    [Fact]
    public void ParseFirstEvent_WithLineUnfoldingAndEscapes_ParsesCorrectly()
    {
        var ics = "BEGIN:VCALENDAR\r\n" +
                  "BEGIN:VEVENT\r\n" +
                  "SUMMARY:Sprint Planning\\, Review \r\n" +
                  " and Retrospective\r\n" +
                  "DESCRIPTION:Line 1\\nLine 2\\; with semicolon\r\n" +
                  "LOCATION:Main Office\\, 2nd Floor\r\n" +
                  "DTSTART:20261105T100000\r\n" +
                  "DTEND:20261105T113000\r\n" +
                  "END:VEVENT\r\n" +
                  "END:VCALENDAR";

        var evt = IcsParser.ParseFirstEvent(ics);

        Assert.NotNull(evt);
        Assert.Equal("Sprint Planning, Review and Retrospective", evt.Summary);
        Assert.Equal("Line 1\nLine 2; with semicolon", evt.Description);
        Assert.Equal("Main Office, 2nd Floor", evt.Location);
    }

    [Fact]
    public void ParseFirstEvent_AllDayEvent_SetsIsAllDayTrue()
    {
        var ics = @"BEGIN:VCALENDAR
BEGIN:VEVENT
SUMMARY:Company Holiday
DTSTART;VALUE=DATE:20261225
DTEND;VALUE=DATE:20261226
END:VEVENT
END:VCALENDAR";

        var evt = IcsParser.ParseFirstEvent(ics);

        Assert.NotNull(evt);
        Assert.Equal("Company Holiday", evt.Summary);
        Assert.True(evt.IsAllDay);
    }

    [Fact]
    public void ParseFirstEvent_OrganizerWithoutCommonName_FallsBackToEmail()
    {
        var ics = @"BEGIN:VCALENDAR
BEGIN:VEVENT
SUMMARY:Quick Sync
ORGANIZER:mailto:john.doe@company.com
END:VEVENT
END:VCALENDAR";

        var evt = IcsParser.ParseFirstEvent(ics);

        Assert.NotNull(evt);
        Assert.Equal("john.doe@company.com", evt.Organizer);
    }

    [Fact]
    public void ParseFirstEvent_NoVEvent_ReturnsNull()
    {
        var ics = @"BEGIN:VCALENDAR
VERSION:2.0
END:VCALENDAR";

        var evt = IcsParser.ParseFirstEvent(ics);

        Assert.Null(evt);
    }

    [Fact]
    public void ParseFirstEvent_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(IcsParser.ParseFirstEvent(null!));
        Assert.Null(IcsParser.ParseFirstEvent("   "));
    }

    [Fact]
    public void ToCalendarEvent_MapsFieldsAndConvertsUtcToLocal()
    {
        var data = new IcsEventData
        {
            Summary = "Architecture Review",
            Description = "Discuss microservice transitions",
            Location = "Boardroom",
            Start = new DateTime(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc),
            IsAllDay = false,
            Organizer = "Alice Smith"
        };

        var calEvent = IcsParser.ToCalendarEvent(data, "emp-42");

        Assert.NotNull(calEvent);
        Assert.Equal("Architecture Review", calEvent.Title);
        Assert.Equal("Discuss microservice transitions", calEvent.Description);
        Assert.Equal("Boardroom", calEvent.Location);
        Assert.Equal("emp-42", calEvent.EmployeeId);
        Assert.Equal("Alice Smith", calEvent.ExternalParticipant);
        Assert.Equal(DateTimeKind.Local, calEvent.Start.Kind);
        Assert.Equal(DateTimeKind.Local, calEvent.End.Kind);
    }
}
