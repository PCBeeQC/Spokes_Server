using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Core;

public class UsageStatisticsTests
{
    [Fact]
    public void UsageStatistics_Properties_WorkCorrectly()
    {
        var stats = new UsageStatistics
        {
            ServerId = "srv_001",
            LicenseId = "lic_enterprise",
            OS = "Linux 6.8",
            ActiveUsers = 25,
            ServerEdition = "Enterprise",
            ServerVersion = "2.4.0",
            AdditionalData = new Dictionary<string, string> { ["Arch"] = "x64" }
        };

        Assert.Equal("srv_001", stats.ServerId);
        Assert.Equal("lic_enterprise", stats.LicenseId);
        Assert.Equal("Linux 6.8", stats.OS);
        Assert.Equal(25, stats.ActiveUsers);
        Assert.Equal("Enterprise", stats.ServerEdition);
        Assert.Equal("2.4.0", stats.ServerVersion);
        Assert.Equal("x64", stats.AdditionalData["Arch"]);

        var json = JsonSerializer.Serialize(stats);
        var copy = JsonSerializer.Deserialize<UsageStatistics>(json);
        Assert.NotNull(copy);
        Assert.Equal(25, copy.ActiveUsers);
    }
}
