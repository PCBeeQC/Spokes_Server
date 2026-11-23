using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Models.Core;

public class OpenIdAccountTests
{
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
