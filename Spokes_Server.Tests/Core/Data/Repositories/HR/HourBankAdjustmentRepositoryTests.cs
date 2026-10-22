using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class HourBankAdjustmentRepositoryTests : RepositoryTestFixture<HourBankAdjustmentRepository>
{
    [Fact]
    public void SaveAndGetById_ReturnsStoredAdjustment()
    {
        var adj = new HourBankAdjustment
        {
            Id = "adj_1",
            EmployeeId = "emp_10",
            Hours = 4.5m,
            Reason = "Overtime adjustment",
            Date = DateTime.UtcNow,
            CreatedByEmployeeId = "admin_1"
        };

        _repo.Save(adj);

        var retrieved = _repo.GetById("adj_1");
        Assert.NotNull(retrieved);
        Assert.Equal("adj_1", retrieved.Id);
        Assert.Equal("emp_10", retrieved.EmployeeId);
        Assert.Equal(4.5m, retrieved.Hours);
    }

    [Fact]
    public void GetByEmployeeId_FiltersCorrectly()
    {
        _repo.Save(new HourBankAdjustment { Id = "a1", EmployeeId = "empA", Hours = 2.0m });
        _repo.Save(new HourBankAdjustment { Id = "a2", EmployeeId = "empB", Hours = 3.0m });
        _repo.Save(new HourBankAdjustment { Id = "a3", EmployeeId = "empA", Hours = -1.0m });

        var resultsEmpA = _repo.GetByEmployeeId("empA");
        var resultsEmpB = _repo.GetByEmployeeId("empB");

        Assert.Equal(2, resultsEmpA.Count);
        Assert.Contains(resultsEmpA, a => a.Id == "a1");
        Assert.Contains(resultsEmpA, a => a.Id == "a3");

        Assert.Single(resultsEmpB);
        Assert.Equal("a2", resultsEmpB[0].Id);
    }

    [Fact]
    public void Delete_RemovesItemFromRepository()
    {
        var adj = new HourBankAdjustment { Id = "del_me", EmployeeId = "emp_x", Hours = 1.0m };
        _repo.Save(adj);
        Assert.NotNull(_repo.GetById("del_me"));

        _repo.Delete("del_me");

        Assert.Null(_repo.GetById("del_me"));
    }
}
