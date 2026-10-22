using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class ReportedMessageRepositoryTests : RepositoryTestFixture<ReportedMessageRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsReportedMessage()
    {
        var report = new ReportedMessage
        {
            Id = "rep-1",
            MessageId = "msg-123",
            ReporterId = "userA",
            ReportedUserId = "userBad",
            Reason = "Inappropriate language"
        };

        _repo.Save(report);

        var found = _repo.GetById("rep-1");
        Assert.NotNull(found);
        Assert.Equal("msg-123", found.MessageId);
        Assert.Equal("userA", found.ReporterId);
        Assert.Equal("userBad", found.ReportedUserId);
        Assert.Equal("Inappropriate language", found.Reason);
    }

    [Fact]
    public void GetFilePath_SavesInReportedMessagesDirectory()
    {
        var report = new ReportedMessage
        {
            Id = "rep-disk",
            MessageId = "msg-disk",
            ReporterId = "userB"
        };

        _repo.Save(report);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Communication", "ReportedMessages", "rep-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetAll_ReturnsAllReports()
    {
        var r1 = new ReportedMessage { Id = "r1", MessageId = "m1" };
        var r2 = new ReportedMessage { Id = "r2", MessageId = "m2" };

        _repo.Save(r1);
        _repo.Save(r2);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Id == "r1");
        Assert.Contains(all, r => r.Id == "r2");
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var report = new ReportedMessage { Id = "rep-del", MessageId = "m-del" };
        _repo.Save(report);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Communication", "ReportedMessages", "rep-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("rep-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("rep-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_PopulatesReportsFromDisk()
    {
        var dir = Path.Combine(_testDataDir, "Communication", "ReportedMessages");
        Directory.CreateDirectory(dir);

        var report = new ReportedMessage { Id = "disk-report", MessageId = "disk-msg", Reason = "Spam" };
        File.WriteAllText(Path.Combine(dir, "disk-report.json"), JsonSerializer.Serialize(report));

        var newRepo = new ReportedMessageRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("disk-report");
        Assert.NotNull(loaded);
        Assert.Equal("disk-msg", loaded.MessageId);
        Assert.Equal("Spam", loaded.Reason);
    }
}
