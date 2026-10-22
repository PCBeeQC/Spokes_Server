using System.Text.Json;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Core;

public class ClientInfoTests
{
    [Fact]
    public void ClientInfo_Properties_WorkCorrectly()
    {
        var client = new ClientInfo
        {
            BusinessName = "Acme Corp",
            ContactPersonName = "John Doe",
            ContactPersonEmail = "john@acme.com",
            ContactPersonPhone = "555-1234",
            BusinessAddressNumber = "12",
            BusinessAddressStreet = "123 Main St",
            BusinessAddressCity = "Metropolis",
            BusinessAddressState = "NY",
            BusinessAddressZip = "10001",
            BusinessAddressCountry = "USA"
        };

        Assert.Equal("Acme Corp", client.BusinessName);
        Assert.Equal("John Doe", client.ContactPersonName);
        Assert.Equal("john@acme.com", client.ContactPersonEmail);
        Assert.Equal("12", client.BusinessAddressNumber);
        Assert.Equal("123 Main St", client.BusinessAddressStreet);
        Assert.Equal("Metropolis", client.BusinessAddressCity);
        Assert.Equal("NY", client.BusinessAddressState);
        Assert.Equal("10001", client.BusinessAddressZip);
        Assert.Equal("USA", client.BusinessAddressCountry);

        var json = JsonSerializer.Serialize(client);
        Assert.Contains("BusinessAddressStreet", json);
        Assert.DoesNotContain("BusinessAdressStreet", json);

        var copy = JsonSerializer.Deserialize<ClientInfo>(json);
        Assert.NotNull(copy);
        Assert.Equal("Acme Corp", copy.BusinessName);
        Assert.Equal("123 Main St", copy.BusinessAddressStreet);
    }

    [Fact]
    public void ClientInfo_DeserializesLegacyTypoFieldNames()
    {
        var legacyJson = """
        {
            "BusinessName": "Legacy Corp",
            "BusinessAdressNumber": "42",
            "BusinessAdressStreet": "Old Street",
            "BusinessAdressCity": "Old City",
            "BusinessAdressState": "QC",
            "BusinessAdressZip": "H1A 1A1",
            "BusinessAdressCountry": "Canada"
        }
        """;

        var client = JsonSerializer.Deserialize<ClientInfo>(legacyJson);
        Assert.NotNull(client);
        Assert.Equal("Legacy Corp", client.BusinessName);
        Assert.Equal("42", client.BusinessAddressNumber);
        Assert.Equal("Old Street", client.BusinessAddressStreet);
        Assert.Equal("Old City", client.BusinessAddressCity);
        Assert.Equal("QC", client.BusinessAddressState);
        Assert.Equal("H1A 1A1", client.BusinessAddressZip);
        Assert.Equal("Canada", client.BusinessAddressCountry);
    }

    [Fact]
    public void PartyContactInfo_PropertiesAndInheritance_WorkCorrectly()
    {
        var party = new PartyContactInfo
        {
            BusinessName = "Universal Parts",
            ContactPersonName = "Alice",
            BusinessAddressCity = "Montreal"
        };

        Assert.Equal("Universal Parts", party.BusinessName);
        Assert.Equal("Alice", party.ContactPersonName);
        Assert.Equal("Montreal", party.BusinessAddressCity);

        // Verify ClientInfo is assignable to PartyContactInfo
        var client = new ClientInfo { BusinessName = "Client A" };
        Assert.IsAssignableFrom<PartyContactInfo>(client);
    }

    [Fact]
    public void PartyContactInfo_DeserializesLegacyTypoFieldNames()
    {
        var legacyJson = """
        {
            "BusinessName": "Legacy Supplier",
            "BusinessAdressNumber": "99",
            "BusinessAdressStreet": "Industrial Way"
        }
        """;

        var party = JsonSerializer.Deserialize<PartyContactInfo>(legacyJson);
        Assert.NotNull(party);
        Assert.Equal("Legacy Supplier", party.BusinessName);
        Assert.Equal("99", party.BusinessAddressNumber);
        Assert.Equal("Industrial Way", party.BusinessAddressStreet);
    }
}

