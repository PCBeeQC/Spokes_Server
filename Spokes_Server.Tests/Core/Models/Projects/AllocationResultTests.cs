using System.Text.Json;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class AllocationResultTests
{
    [Fact]
    public void AllocationResult_Properties_WorkCorrectly()
    {
        var result = new AllocationResult
        {
            EmployeeId = "emp_dev",
            Hours = 12.5m,
            Note = "Backend API development"
        };

        Assert.Equal("emp_dev", result.EmployeeId);
        Assert.Equal(12.5m, result.Hours);
        Assert.Equal("Backend API development", result.Note);

        var json = JsonSerializer.Serialize(result);
        var copy = JsonSerializer.Deserialize<AllocationResult>(json);
        Assert.NotNull(copy);
        Assert.Equal(12.5m, copy.Hours);
    }
}
