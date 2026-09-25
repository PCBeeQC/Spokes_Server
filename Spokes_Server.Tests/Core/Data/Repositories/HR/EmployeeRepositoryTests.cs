using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class EmployeeRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly EmployeeRepository _repo;

    public EmployeeRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Employees_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new EmployeeRepository(_writer, mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { Console.WriteLine($"Cleanup failed: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new EmployeeRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_And_GetById_ReturnsEmployee()
    {
        var emp = new Employee
        {
            Id = "emp1",
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@example.com"
        };

        _repo.Save(emp);

        var found = _repo.GetById("emp1");
        Assert.NotNull(found);
        Assert.Equal("Alice Smith", found.FullName);
        Assert.Equal("alice@example.com", found.Email);
    }

    [Fact]
    public void GetFilePath_SavesInSubfolderProfileJson()
    {
        var emp = new Employee
        {
            Id = "emp2",
            FirstName = "Bob",
            LastName = "Jones"
        };

        _repo.Save(emp);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp2", "profile.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllEmployees()
    {
        var e1 = new Employee { Id = "e1", FirstName = "One" };
        var e2 = new Employee { Id = "e2", FirstName = "Two" };

        _repo.Save(e1);
        _repo.Save(e2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == "e1");
        Assert.Contains(all, e => e.Id == "e2");
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var emp = new Employee { Id = "emp-del", FirstName = "DeleteMe" };
        _repo.Save(emp);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp-del", "profile.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("emp-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("emp-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesEmployees()
    {
        var empDir = Path.Combine(_testDataDir, "Employees", "emp-disk");
        Directory.CreateDirectory(empDir);

        var emp = new Employee { Id = "emp-disk", FirstName = "Disk", LastName = "User" };
        File.WriteAllText(Path.Combine(empDir, "profile.json"), System.Text.Json.JsonSerializer.Serialize(emp));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new EmployeeRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("emp-disk");
        Assert.NotNull(loaded);
        Assert.Equal("Disk User", loaded.FullName);
    }

    [Fact]
    public void LoadFromDisk_MigratesAvatarBase64_WithDataUriPrefix()
    {
        var empDir = Path.Combine(_testDataDir, "Employees", "emp-mig");
        Directory.CreateDirectory(empDir);

        var rawBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var base64 = Convert.ToBase64String(rawBytes);
        var emp = new Employee
        {
            Id = "emp-mig",
            FirstName = "Migrate",
            AvatarBase64 = $"data:image/png;base64,{base64}"
        };

        File.WriteAllText(Path.Combine(empDir, "profile.json"), System.Text.Json.JsonSerializer.Serialize(emp));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new EmployeeRepository(_writer, mockConfig.Object);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("emp-mig");
        Assert.NotNull(loaded);
        Assert.Null(loaded.AvatarBase64);
        Assert.Equal("avatar.png", loaded.AvatarFile);

        var avatarFileOnDisk = Path.Combine(empDir, "avatar.png");
        Assert.True(File.Exists(avatarFileOnDisk));
        Assert.Equal(rawBytes, File.ReadAllBytes(avatarFileOnDisk));
    }

    [Fact]
    public void LoadFromDisk_WhenAvatarBase64Invalid_HandlesExceptionGracefully()
    {
        var empDir = Path.Combine(_testDataDir, "Employees", "emp-invalid-avatar");
        Directory.CreateDirectory(empDir);

        var emp = new Employee
        {
            Id = "emp-invalid-avatar",
            FirstName = "InvalidAvatar",
            AvatarBase64 = "this-is-not-valid-base64!!!"
        };

        File.WriteAllText(Path.Combine(empDir, "profile.json"), System.Text.Json.JsonSerializer.Serialize(emp));

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var newRepo = new EmployeeRepository(_writer, mockConfig.Object);
        // Should not throw
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("emp-invalid-avatar");
        Assert.NotNull(loaded);
    }

    [Fact]
    public void GetActiveEmployees_ExcludesInactiveSystemSuspendedAndBanned()
    {
        var active = new Employee { Id = "emp-act", FirstName = "Active", IsActive = true };
        var inactive = new Employee { Id = "emp-inact", FirstName = "Inactive", IsActive = false };
        var systemUser = new Employee { Id = "emp-sys", FirstName = "System", IsActive = true, IsSystem = true };
        var suspended = new Employee { Id = "emp-susp", FirstName = "Suspended", IsActive = true, IsSuspended = true };
        var banned = new Employee { Id = "emp-ban", FirstName = "Banned", IsActive = true, IsBanned = true };

        _repo.Save(active);
        _repo.Save(inactive);
        _repo.Save(systemUser);
        _repo.Save(suspended);
        _repo.Save(banned);

        var activeEmployees = _repo.GetActiveEmployees();

        Assert.Single(activeEmployees);
        Assert.Contains(activeEmployees, e => e.Id == "emp-act");
        Assert.DoesNotContain(activeEmployees, e => e.Id == "emp-inact");
        Assert.DoesNotContain(activeEmployees, e => e.Id == "emp-sys");
        Assert.DoesNotContain(activeEmployees, e => e.Id == "emp-susp");
        Assert.DoesNotContain(activeEmployees, e => e.Id == "emp-ban");
    }
}
