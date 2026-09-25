using System;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ContactPersonTests
{
        [Fact]
        public void ContactPerson_Defaults_AreSetCorrectly()
        {
            var contact = new ContactPerson();

            Assert.False(string.IsNullOrWhiteSpace(contact.Id));
            Assert.True(Guid.TryParse(contact.Id, out _));
            Assert.Equal(string.Empty, contact.Prefix);
            Assert.Equal(string.Empty, contact.Name);
            Assert.Equal(string.Empty, contact.Title);
            Assert.Equal(string.Empty, contact.Email);
            Assert.Equal(string.Empty, contact.Phone);
            Assert.False(contact.IsPrimary);
        }

        [Fact]
        public void ContactPerson_PropertyMutations_WorkAsExpected()
        {
            var newId = Guid.NewGuid().ToString();
            var contact = new ContactPerson
            {
                Id = newId,
                Prefix = "Dr.",
                Name = "Jane Doe",
                Title = "Lead Architect",
                Email = "jane.doe@example.com",
                Phone = "+1-555-0199",
                IsPrimary = true
            };

            Assert.Equal(newId, contact.Id);
            Assert.Equal("Dr.", contact.Prefix);
            Assert.Equal("Jane Doe", contact.Name);
            Assert.Equal("Lead Architect", contact.Title);
            Assert.Equal("jane.doe@example.com", contact.Email);
            Assert.Equal("+1-555-0199", contact.Phone);
            Assert.True(contact.IsPrimary);
        }

        [Fact]
        public void ContactPerson_Equals_ReturnsTrueForReferenceEquality()
        {
            var contact = new ContactPerson();

            Assert.True(contact.Equals(contact));
        }

        [Fact]
        public void ContactPerson_Equals_ReturnsTrueForSameId()
        {
            var id = Guid.NewGuid().ToString();
            var contact1 = new ContactPerson { Id = id, Name = "Alice" };
            var contact2 = new ContactPerson { Id = id, Name = "Bob" };

            Assert.True(contact1.Equals(contact2));
            Assert.Equal(contact1.GetHashCode(), contact2.GetHashCode());
        }

        [Fact]
        public void ContactPerson_Equals_ReturnsFalseForDifferentId()
        {
            var contact1 = new ContactPerson { Id = "id-1" };
            var contact2 = new ContactPerson { Id = "id-2" };

            Assert.False(contact1.Equals(contact2));
        }

        [Fact]
        public void ContactPerson_Equals_ReturnsFalseForNull()
        {
            var contact = new ContactPerson();

            Assert.False(contact.Equals(null));
        }

        [Fact]
        public void ContactPerson_Equals_ReturnsFalseForDifferentType()
        {
            var contact = new ContactPerson();
            var otherObject = new object();

            Assert.False(contact.Equals(otherObject));
            Assert.False(contact.Equals("some string"));
        }

        [Fact]
        public void ContactPerson_GetHashCode_WhenIdIsNull_DoesNotThrow()
        {
            var contact = new ContactPerson { Id = null! };

            var hashCode = contact.GetHashCode();

            Assert.IsType<int>(hashCode);
        }
    }
