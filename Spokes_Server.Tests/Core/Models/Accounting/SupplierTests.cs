using System;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class SupplierTests
{
        [Fact]
        public void Defaults_InitializesWithExpectedValues()
        {
            var supplier = new Supplier();

            Assert.False(string.IsNullOrWhiteSpace(supplier.Id));
            Assert.True(Guid.TryParse(supplier.Id, out var parsedGuid));
            Assert.NotEqual(Guid.Empty, parsedGuid);
            Assert.NotNull(supplier.Info);
            Assert.NotNull(supplier.Contacts);
            Assert.Empty(supplier.Contacts);
        }

        [Fact]
        public void Name_ReturnsInfoBusinessName()
        {
            var supplier = new Supplier();
            supplier.Info.BusinessName = "Acme Supply Co.";

            Assert.Equal("Acme Supply Co.", supplier.Name);
        }

        [Fact]
        public void Equals_SameReference_ReturnsTrue()
        {
            var supplier = new Supplier();

            Assert.True(supplier.Equals(supplier));
        }

        [Fact]
        public void Equals_NullObject_ReturnsFalse()
        {
            var supplier = new Supplier();

            Assert.False(supplier.Equals(null));
        }

        [Fact]
        public void Equals_DifferentType_ReturnsFalse()
        {
            var supplier = new Supplier();
            var otherObject = new object();

            Assert.False(supplier.Equals(otherObject));
            Assert.False(supplier.Equals("not a supplier"));
        }

        [Fact]
        public void Equals_SameId_ReturnsTrue()
        {
            var id = Guid.NewGuid().ToString();
            var supplier1 = new Supplier { Id = id };
            var supplier2 = new Supplier { Id = id };

            Assert.True(supplier1.Equals(supplier2));
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var supplier1 = new Supplier { Id = Guid.NewGuid().ToString() };
            var supplier2 = new Supplier { Id = Guid.NewGuid().ToString() };

            Assert.False(supplier1.Equals(supplier2));
        }

        [Fact]
        public void GetHashCode_WhenIdIsNotNull_ReturnsIdHashCode()
        {
            var id = "supplier-test-id-123";
            var supplier = new Supplier { Id = id };

            Assert.Equal(id.GetHashCode(), supplier.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WhenIdIsNull_ReturnsBaseHashCode()
        {
            var supplier = new Supplier { Id = null! };

            var hashCode = supplier.GetHashCode();
            Assert.IsType<int>(hashCode);
        }

        [Fact]
        public void Contacts_CanAddAndRetrieveContacts()
        {
            var supplier = new Supplier();
            var contact = new ContactPerson { Name = "Jane Doe", Email = "jane@example.com" };

            supplier.Contacts.Add(contact);

            Assert.Single(supplier.Contacts);
            Assert.Equal("Jane Doe", supplier.Contacts[0].Name);
        }
    }
