using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class ClientPoTests
{
    [Fact]
    public void ClientPo_DefaultValuesAndProperties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var po = new ClientPo
        {
            Id = "po_1",
            PoNumber = "PO-12345",
            Amount = 5000m,
            FilePath = "files/po-12345.pdf",
            DateReceived = now,
            MimeType = "application/pdf"
        };

        Assert.Equal("po_1", po.Id);
        Assert.Equal("PO-12345", po.PoNumber);
        Assert.Equal(5000m, po.Amount);
        Assert.Equal("files/po-12345.pdf", po.FilePath);
        Assert.Equal(now, po.DateReceived);
        Assert.Equal("application/pdf", po.MimeType);

        var json = JsonSerializer.Serialize(po);
        var deserialized = JsonSerializer.Deserialize<ClientPo>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("PO-12345", deserialized.PoNumber);
        Assert.Equal(5000m, deserialized.Amount);
    }
}
