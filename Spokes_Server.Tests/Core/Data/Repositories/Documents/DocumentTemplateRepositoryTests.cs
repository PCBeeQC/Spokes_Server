using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Documents;

public class DocumentTemplateRepositoryTests : RepositoryTestFixture<DocumentTemplateRepository>
{
    [Fact]
    public void GetDefault_ReturnsDefaultTemplateWhenConfigured()
    {
        var t1 = new DocumentTemplate { Id = "t1", Name = "Standard", IsDefault = false };
        var t2 = new DocumentTemplate { Id = "t2", Name = "Official Default", IsDefault = true };

        _repo.Save(t1);
        _repo.Save(t2);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("t2", def.Id);
        Assert.True(def.IsDefault);
    }

    [Fact]
    public void GetDefault_ReturnsFirstTemplateWhenNoDefaultConfigured()
    {
        var t1 = new DocumentTemplate { Id = "t1", Name = "Only Template", IsDefault = false };
        _repo.Save(t1);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("t1", def.Id);
    }

    [Fact]
    public void GetDefault_ReturnsNewInstanceWhenEmpty()
    {
        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.NotNull(def.Id);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsTemplatesFolder()
    {
        var template = new DocumentTemplate { Id = "tmpl-disk", Name = "Disk Template" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Templates", "tmpl-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var template = new DocumentTemplate { Id = "tmpl-del", Name = "To Delete" };
        _repo.Save(template);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "Templates", "tmpl-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("tmpl-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("tmpl-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
