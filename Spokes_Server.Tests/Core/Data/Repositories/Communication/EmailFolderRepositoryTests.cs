using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class EmailFolderRepositoryTests : RepositoryTestFixture<EmailFolderRepository>
{
    [Fact]
    public void Save_AddsToCache()
    {
        var folder = new EmailFolder { Id = "f1", AccountId = "emp1", Name = "Inbox", Path = "INBOX" };
        _repo.Save(folder);

        var folders = _repo.GetByAccount("emp1");
        Assert.Single(folders);
        Assert.Equal("Inbox", folders.First().Name);
    }

    [Fact]
    public void LoadFromDisk_LoadsCorrectly()
    {
        // Set up directory structure: Data/EmailAccounts/{AccountId}/Folders/{Id}.json
        var empDir = Path.Combine(_testDataDir, "EmailAccounts", "emp2", "Folders");
        Directory.CreateDirectory(empDir);

        var folder1 = new EmailFolder { Id = "f1", AccountId = "emp2", Name = "Sent" };
        var folder2 = new EmailFolder { Id = "f2", AccountId = "emp2", Name = "Trash" };

        File.WriteAllText(Path.Combine(empDir, "f1.json"), JsonSerializer.Serialize(folder1));
        File.WriteAllText(Path.Combine(empDir, "f2.json"), JsonSerializer.Serialize(folder2));

        // Also create some corrupted file to test the try-catch block
        File.WriteAllText(Path.Combine(empDir, "corrupted.json"), "{ invalid json");

        _repo.LoadFromDisk();

        var loaded = _repo.GetByAccount("emp2");
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, f => f.Id == "f1");
        Assert.Contains(loaded, f => f.Id == "f2");
    }

    [Fact]
    public void LoadFromDisk_IgnoresMissingSubdirectories()
    {
        // Just the EmailAccounts folder, no Email/Folders inside
        var empDir = Path.Combine(_testDataDir, "EmailAccounts", "emp3");
        Directory.CreateDirectory(empDir);

        _repo.LoadFromDisk();

        var loaded = _repo.GetByAccount("emp3");
        Assert.Empty(loaded);
    }

    [Fact]
    public void GetFilePath_ResolvesUnderAccountEmailFolders()
    {
        var folder = new EmailFolder { Id = "f-disk", AccountId = "emp-path", Name = "Archive" };

        _repo.Save(folder);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "EmailAccounts", "emp-path", "Folders", "f-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFolderFromCache()
    {
        var folder = new EmailFolder { Id = "f-del", AccountId = "emp-del", Name = "Temp" };
        _repo.Save(folder);

        Assert.Single(_repo.GetByAccount("emp-del"));

        _repo.Delete("f-del");

        Assert.Empty(_repo.GetByAccount("emp-del"));
    }

    [Fact]
    public void GetByAccount_WhenNoFoldersMatch_ReturnsEmptyList()
    {
        var folder = new EmailFolder { Id = "f-other", AccountId = "emp-other", Name = "Inbox" };
        _repo.Save(folder);

        var result = _repo.GetByAccount("emp-none");
        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
