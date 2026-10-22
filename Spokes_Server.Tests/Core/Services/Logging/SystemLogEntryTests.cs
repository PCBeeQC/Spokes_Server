using System;
using Spokes_Server.Core.Services.Logging;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Logging;

public class SystemLogEntryTests
{
    [Fact]
    public void SystemLogEntry_DefaultsAndPropertyAssignments_WorkAsExpected()
    {
        var entry = new SystemLogEntry();
        Assert.Equal("Info", entry.Severity);
        Assert.Equal(string.Empty, entry.Category);
        Assert.Equal(string.Empty, entry.Message);
        Assert.Null(entry.Details);

        var now = DateTime.UtcNow;
        entry.TimestampUtc = now;
        entry.Category = "Auth";
        entry.Severity = "Warning";
        entry.Message = "Invalid credentials";
        entry.Details = "IP: 127.0.0.1";

        Assert.Equal(now, entry.TimestampUtc);
        Assert.Equal("Auth", entry.Category);
        Assert.Equal("Warning", entry.Severity);
        Assert.Equal("Invalid credentials", entry.Message);
        Assert.Equal("IP: 127.0.0.1", entry.Details);
    }
}
