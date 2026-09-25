using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Models
{
    public class OpenIdAccountTests
    {
        [Fact]
        public void OpenIdAccount_Initialization_SetsDefaultsCorrectly()
        {
            var account = new OpenIdAccount();

            Assert.False(string.IsNullOrEmpty(account.Id));
            Assert.Equal(string.Empty, account.Sub);
            Assert.Equal(string.Empty, account.Name);
            Assert.Equal(string.Empty, account.Email);
            Assert.Equal(string.Empty, account.LinkedEmployeeId);

            Assert.True(account.FirstSeenAt <= DateTime.UtcNow);
            Assert.True(account.LastLoginAt <= DateTime.UtcNow);

            Assert.False(account.IsLinked);
        }

        [Fact]
        public void OpenIdAccount_IsLinked_ReturnsTrueWhenEmployeeIdSet()
        {
            var account = new OpenIdAccount { LinkedEmployeeId = "emp123" };
            Assert.True(account.IsLinked);
        }

        [Fact]
        public void OpenIdAccount_IsLinked_ReturnsFalseWhenEmployeeIdEmptyOrNull()
        {
            var account1 = new OpenIdAccount { LinkedEmployeeId = string.Empty };
            Assert.False(account1.IsLinked);

            var account2 = new OpenIdAccount { LinkedEmployeeId = null };
            Assert.False(account2.IsLinked);
        }

        [Fact]
        public void OpenIdAccount_Equals_ReturnsTrueForReferenceEquality()
        {
            var account = new OpenIdAccount();
            Assert.True(account.Equals(account));
        }

        [Fact]
        public void OpenIdAccount_Equals_ReturnsTrueForSameId()
        {
            var id = Guid.NewGuid().ToString();
            var account1 = new OpenIdAccount { Id = id, Name = "User 1" };
            var account2 = new OpenIdAccount { Id = id, Name = "User 2" };

            Assert.True(account1.Equals(account2));
        }

        [Fact]
        public void OpenIdAccount_Equals_ReturnsFalseForDifferentId()
        {
            var account1 = new OpenIdAccount { Id = "id-1" };
            var account2 = new OpenIdAccount { Id = "id-2" };

            Assert.False(account1.Equals(account2));
        }

        [Fact]
        public void OpenIdAccount_Equals_ReturnsFalseForNull()
        {
            var account = new OpenIdAccount();
            Assert.False(account.Equals(null));
        }

        [Fact]
        public void OpenIdAccount_Equals_ReturnsFalseForDifferentType()
        {
            var account = new OpenIdAccount();
            Assert.False(account.Equals("some-string"));
            Assert.False(account.Equals(new object()));
        }

        [Fact]
        public void OpenIdAccount_GetHashCode_ReturnsSameHashCodeForSameId()
        {
            var id = "unique-account-id";
            var account1 = new OpenIdAccount { Id = id };
            var account2 = new OpenIdAccount { Id = id };

            Assert.Equal(account1.GetHashCode(), account2.GetHashCode());
        }

        [Fact]
        public void OpenIdAccount_GetHashCode_WhenIdIsNull_DoesNotThrow()
        {
            var account = new OpenIdAccount { Id = null! };
            var hashCode = account.GetHashCode();
            Assert.IsType<int>(hashCode);
        }

        [Fact]
        public void OpenIdAccount_Properties_CanBeSetAndRead()
        {
            var firstSeen = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var lastLogin = new DateTime(2025, 1, 2, 14, 30, 0, DateTimeKind.Utc);

            var account = new OpenIdAccount
            {
                Id = "custom-openid-id",
                Sub = "auth0|123456789",
                Name = "Jane Doe",
                Email = "jane.doe@example.com",
                LinkedEmployeeId = "emp-789",
                FirstSeenAt = firstSeen,
                LastLoginAt = lastLogin
            };

            Assert.Equal("custom-openid-id", account.Id);
            Assert.Equal("auth0|123456789", account.Sub);
            Assert.Equal("Jane Doe", account.Name);
            Assert.Equal("jane.doe@example.com", account.Email);
            Assert.Equal("emp-789", account.LinkedEmployeeId);
            Assert.Equal(firstSeen, account.FirstSeenAt);
            Assert.Equal(lastLogin, account.LastLoginAt);
            Assert.True(account.IsLinked);
        }
    }
}
