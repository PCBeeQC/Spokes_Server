using System;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class SupplierTests
{
        [Fact]
        public void Name_ReturnsContactInfoBusinessName()
        {
            var supplier = new Supplier();
            supplier.ContactInfo.BusinessName = "Acme Supply Co.";

            Assert.Equal("Acme Supply Co.", supplier.Name);
        }

        [Fact]
        public void Equals_And_GetHashCode_Behavior()
        {
            var id = "supplier-test-id-123";
            var supplier1 = new Supplier { Id = id };
            var supplier2 = new Supplier { Id = id };
            var supplier3 = new Supplier { Id = "supplier-other" };
            var nullIdSupplier = new Supplier { Id = null! };

            Assert.True(supplier1.Equals(supplier1));
            Assert.True(supplier1.Equals(supplier2));
            Assert.False(supplier1.Equals(supplier3));
            Assert.False(supplier1.Equals(null));
            Assert.False(supplier1.Equals(new object()));
            Assert.False(supplier1.Equals("not a supplier"));
            Assert.Equal(id.GetHashCode(), supplier1.GetHashCode());
            Assert.Equal(supplier1.GetHashCode(), supplier2.GetHashCode());
            Assert.IsType<int>(nullIdSupplier.GetHashCode());
        }

        [Fact]
        public void Supplier_JsonDeserialization_SupportsLegacyInfoProperty()
        {
            const string legacyJson = """
            {
                "Id": "sup-1",
                "Info": {
                    "BusinessName": "Legacy Components",
                    "ContactPersonEmail": "sales@legacy.com"
                }
            }
            """;

            var supplier = System.Text.Json.JsonSerializer.Deserialize<Supplier>(legacyJson);
            Assert.NotNull(supplier);
            Assert.Equal("Legacy Components", supplier.ContactInfo.BusinessName);
            Assert.Equal("sales@legacy.com", supplier.ContactInfo.ContactPersonEmail);
        }

        [Fact]
        public void Supplier_JsonDeserialization_SupportsContactInfoProperty()
        {
            const string modernJson = """
            {
                "Id": "sup-2",
                "ContactInfo": {
                    "BusinessName": "Modern Parts",
                    "ContactPersonEmail": "orders@modern.com"
                }
            }
            """;

            var supplier = System.Text.Json.JsonSerializer.Deserialize<Supplier>(modernJson);
            Assert.NotNull(supplier);
            Assert.Equal("Modern Parts", supplier.ContactInfo.BusinessName);
            Assert.Equal("orders@modern.com", supplier.ContactInfo.ContactPersonEmail);
        }

        [Fact]
        public void Supplier_JsonSerialization_WritesContactInfoProperty()
        {
            var supplier = new Supplier { Id = "sup-3" };
            supplier.ContactInfo.BusinessName = "Global Logistics";

            var json = System.Text.Json.JsonSerializer.Serialize(supplier);
            Assert.Contains("\"ContactInfo\"", json);
            Assert.DoesNotContain("\"LegacyInfo\"", json);
        }
    }

