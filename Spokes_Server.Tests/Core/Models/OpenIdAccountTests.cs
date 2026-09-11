using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

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
    }
}


