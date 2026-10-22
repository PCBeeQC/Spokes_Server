using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Documents;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Documents;

public class DocumentBlockInstanceTests
{
    [Fact]
    public void DocumentBlockInstance_Properties_WorkCorrectly()
    {
        var block = new DocumentBlockInstance
        {
            Id = "block_1",
            Type = "Signature",
            Name = "Client Signature",
            Properties = new Dictionary<string, string> { ["Required"] = "true" }
        };

        Assert.Equal("block_1", block.Id);
        Assert.Equal("Signature", block.Type);
        Assert.Equal("Client Signature", block.Name);
        Assert.Equal("true", block.Properties["Required"]);

        var json = JsonSerializer.Serialize(block);
        var copy = JsonSerializer.Deserialize<DocumentBlockInstance>(json);
        Assert.NotNull(copy);
        Assert.Equal("block_1", copy.Id);
        Assert.Equal("Client Signature", copy.Name);
    }
}
