using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Documents;

public class TermsAndConditionsRepositoryTests : RepositoryTestFixture<TermsAndConditionsRepository>
{
    [Fact]
    public void GetDefault_ReturnsDefaultTermsWhenConfigured()
    {
        var t1 = new TermsAndConditions { Id = "tc1", Name = "Standard", IsDefault = false };
        var t2 = new TermsAndConditions { Id = "tc2", Name = "Official Default", IsDefault = true };

        _repo.Save(t1);
        _repo.Save(t2);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("tc2", def.Id);
        Assert.True(def.IsDefault);
    }

    [Fact]
    public void GetDefault_ReturnsFirstWhenNoDefaultConfigured()
    {
        var t1 = new TermsAndConditions { Id = "tc1", Name = "Only Terms", IsDefault = false };
        _repo.Save(t1);

        var def = _repo.GetDefault();
        Assert.NotNull(def);
        Assert.Equal("tc1", def.Id);
    }

    [Fact]
    public void GetDefault_ReturnsNullWhenEmpty()
    {
        var def = _repo.GetDefault();
        Assert.Null(def);
    }

    [Fact]
    public void EnsureSeeded_SeedsDefaultWhenEmpty()
    {
        var seeded = _repo.EnsureSeeded("Custom seeded terms");
        Assert.NotNull(seeded);
        Assert.Equal("Custom seeded terms", seeded.Content);
        Assert.True(seeded.IsDefault);

        _writer.FlushAll();
        var expectedPath = Path.Combine(_testDataDir, "Settings", "TermsAndConditions", $"{seeded.Id}.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetFilePath_SavesInSettingsTermsAndConditionsFolder()
    {
        var terms = new TermsAndConditions { Id = "tc-disk", Name = "Disk Terms" };
        _repo.Save(terms);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "TermsAndConditions", "tc-disk.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Delete_RemovesFromCacheAndDisk()
    {
        var terms = new TermsAndConditions { Id = "tc-del", Name = "To Delete" };
        _repo.Save(terms);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Settings", "TermsAndConditions", "tc-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("tc-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("tc-del"));
        Assert.False(File.Exists(expectedPath));
    }
}
