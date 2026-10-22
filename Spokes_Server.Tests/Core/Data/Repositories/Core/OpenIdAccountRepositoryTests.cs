using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class OpenIdAccountRepositoryTests : RepositoryTestFixture<OpenIdAccountRepository>
{
    [Fact]
    public void GetBySub_ReturnsMatchingAccount()
    {
        var acc = new OpenIdAccount { Id = "acc1", Sub = "sub-google-12345", Email = "user@example.com" };
        _repo.Save(acc);

        var found = _repo.GetBySub("sub-google-12345");
        Assert.NotNull(found);
        Assert.Equal("acc1", found.Id);
        Assert.Equal("user@example.com", found.Email);

        var notFound = _repo.GetBySub("nonexistent-sub");
        Assert.Null(notFound);
    }

    [Fact]
    public void GetByEmployeeId_ReturnsAllAccountsLinkedToEmployee()
    {
        var acc1 = new OpenIdAccount { Id = "acc-emp-1", Sub = "sub1", LinkedEmployeeId = "emp100" };
        var acc2 = new OpenIdAccount { Id = "acc-emp-2", Sub = "sub2", LinkedEmployeeId = "emp100" };
        var acc3 = new OpenIdAccount { Id = "acc-emp-3", Sub = "sub3", LinkedEmployeeId = "emp200" };

        _repo.Save(acc1);
        _repo.Save(acc2);
        _repo.Save(acc3);

        var emp100Accounts = _repo.GetByEmployeeId("emp100");
        Assert.Equal(2, emp100Accounts.Count);
        Assert.Contains(emp100Accounts, a => a.Id == "acc-emp-1");
        Assert.Contains(emp100Accounts, a => a.Id == "acc-emp-2");
    }

    [Fact]
    public void GetUnlinked_ReturnsOnlyAccountsWithoutLinkedEmployeeId()
    {
        var acc1 = new OpenIdAccount { Id = "unlinked1", Sub = "sub-u1", LinkedEmployeeId = string.Empty };
        var acc2 = new OpenIdAccount { Id = "unlinked2", Sub = "sub-u2", LinkedEmployeeId = null! };
        var acc3 = new OpenIdAccount { Id = "linked", Sub = "sub-l1", LinkedEmployeeId = "emp1" };

        _repo.Save(acc1);
        _repo.Save(acc2);
        _repo.Save(acc3);

        var unlinked = _repo.GetUnlinked();
        Assert.Equal(2, unlinked.Count);
        Assert.Contains(unlinked, a => a.Id == "unlinked1");
        Assert.Contains(unlinked, a => a.Id == "unlinked2");
        Assert.DoesNotContain(unlinked, a => a.Id == "linked");
    }

    [Fact]
    public void GetFilePath_SavesInOpenIdAccountsFolder()
    {
        var acc = new OpenIdAccount { Id = "acc-disk", Sub = "sub-disk" };
        _repo.Save(acc);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "OpenIdAccounts", "acc-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var acc = new OpenIdAccount { Id = "acc-del", Sub = "sub-del" };
        _repo.Save(acc);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "OpenIdAccounts", "acc-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("acc-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("acc-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
