using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class SubTaskTests
{
    [Fact]
    public void SubTask_Properties_WorkCorrectly()
    {
        var sub = new SubTask
        {
            Id = "sub_1",
            Name = "Code Review",
            NameTranslations = new Dictionary<string, string> { ["fr"] = "Revue de code" }
        };

        Assert.Equal("sub_1", sub.Id);
        Assert.Equal("Code Review", sub.Name);
        Assert.Equal("Revue de code", sub.NameTranslations["fr"]);

        var json = JsonSerializer.Serialize(sub);
        var copy = JsonSerializer.Deserialize<SubTask>(json);
        Assert.NotNull(copy);
        Assert.Equal("Code Review", copy.Name);
    }
}
