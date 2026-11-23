using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ContactPersonTests
{
        [Fact]
        public void ContactPerson_Equals_And_GetHashCode_Behavior()
        {
            var id = Guid.NewGuid().ToString();
            var contact1 = new ContactPerson { Id = id, Name = "Alice" };
            var contact2 = new ContactPerson { Id = id, Name = "Bob" };
            var contact3 = new ContactPerson { Id = "id-2" };
            var nullIdContact = new ContactPerson { Id = null! };

            Assert.True(contact1.Equals(contact1));
            Assert.True(contact1.Equals(contact2));
            Assert.Equal(contact1.GetHashCode(), contact2.GetHashCode());
            Assert.False(contact1.Equals(contact3));
            Assert.False(contact1.Equals(null));
            Assert.False(contact1.Equals(new object()));
            Assert.False(contact1.Equals("some string"));
            Assert.IsType<int>(nullIdContact.GetHashCode());
        }
    }
