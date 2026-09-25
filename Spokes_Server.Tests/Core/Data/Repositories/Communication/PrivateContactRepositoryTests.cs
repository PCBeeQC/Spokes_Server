using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class PrivateContactRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly PrivateContactRepository _repo;
    private readonly Mock<ILogger<PrivateContactRepository>> _loggerMock;

    public PrivateContactRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_PrivateContacts_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _loggerMock = new Mock<ILogger<PrivateContactRepository>>();

        _repo = new PrivateContactRepository(_writer, mockConfig.Object, _loggerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    [Fact]
    public void Save_AddsToCacheForEmployee()
    {
        var contact = new ContactPerson { Id = "c1", Name = "John Doe" };
        _repo.Save("emp1", contact);

        var contacts = _repo.GetAllForEmployee("emp1");
        Assert.Single(contacts);
        Assert.Equal("John Doe", contacts.First().Name);

        var byId = _repo.GetById("emp1", "c1");
        Assert.NotNull(byId);
    }

    [Fact]
    public void LoadFromDisk_LoadsCorrectly()
    {
        var contactsDir = Path.Combine(_testDataDir, "Employees", "emp2", "Contacts");
        Directory.CreateDirectory(contactsDir);

        var contact1 = new ContactPerson { Id = "c1", Name = "Alice" };
        var contact2 = new ContactPerson { Id = "c2", Name = "Bob" };

        File.WriteAllText(Path.Combine(contactsDir, "c1.json"), JsonSerializer.Serialize(contact1));
        File.WriteAllText(Path.Combine(contactsDir, "c2.json"), JsonSerializer.Serialize(contact2));
        File.WriteAllText(Path.Combine(contactsDir, "invalid.json"), "invalid json");

        _repo.LoadFromDisk();

        var contacts = _repo.GetAllForEmployee("emp2");
        Assert.Equal(2, contacts.Count);
        Assert.Contains(contacts, c => c.Name == "Alice");
        Assert.Contains(contacts, c => c.Name == "Bob");

        // Verify empty dir is handled properly
        Assert.Empty(_repo.GetAllForEmployee("unknown"));
    }

    [Fact]
    public void Delete_RemovesFromCache()
    {
        var contact = new ContactPerson { Id = "c3" };
        _repo.Save("emp3", contact);

        Assert.NotNull(_repo.GetById("emp3", "c3"));

        _repo.Delete("emp3", "c3");

        Assert.Null(_repo.GetById("emp3", "c3"));
        Assert.Empty(_repo.GetAllForEmployee("emp3"));
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        var contact = new ContactPerson { Id = "c4" };
        _repo.Save("emp4", contact);

        Assert.NotEmpty(_repo.GetAllForEmployee("emp4"));

        _repo.Clear();

        Assert.Empty(_repo.GetAllForEmployee("emp4"));
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new PrivateContactRepository(_writer, mockConfig.Object, _loggerMock.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Save_WritesToDiskInEmployeeContactsDirectory()
    {
        var contact = new ContactPerson { Id = "c-disk", Name = "Disk Contact" };
        _repo.Save("emp-disk", contact);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp-disk", "Contacts", "c-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFileFromDisk()
    {
        var contact = new ContactPerson { Id = "c-del-disk", Name = "To Delete" };
        _repo.Save("emp-del", contact);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp-del", "Contacts", "c-del-disk.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("emp-del", "c-del-disk");
        _writer.FlushAll();

        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void GetById_WhenEmployeeOrContactMissing_ReturnsNull()
    {
        Assert.Null(_repo.GetById("non-existent-emp", "any-contact"));

        var contact = new ContactPerson { Id = "c-exists", Name = "Exists" };
        _repo.Save("emp-partial", contact);

        Assert.Null(_repo.GetById("emp-partial", "non-existent-contact"));
    }
}




