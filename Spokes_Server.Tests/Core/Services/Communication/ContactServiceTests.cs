using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class ContactServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IContactService _service;

    public ContactServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Contact_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IContactService, ContactService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _service = _serviceProvider.GetRequiredService<IContactService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void ExistsInPublic_ReturnsCorrectResult()
    {
        var contact = new ContactPerson { Id = "c-pub-1", Name = "Public User", Email = "public@example.com" };
        _service.SavePublicContact(contact);

        Assert.True(_service.ExistsInPublic("public@example.com"));
        Assert.True(_service.ExistsInPublic("PUBLIC@EXAMPLE.COM"));
        Assert.False(_service.ExistsInPublic("other@example.com"));
        Assert.False(_service.ExistsInPublic(""));
        Assert.False(_service.ExistsInPublic(null!));
    }

    [Fact]
    public void ExistsInPrivate_ReturnsCorrectResultPerEmployee()
    {
        var contact = new ContactPerson { Id = "c-priv-1", Name = "Private User", Email = "private@example.com" };
        _service.SavePrivateContact("emp-1", contact);

        Assert.True(_service.ExistsInPrivate("emp-1", "private@example.com"));
        Assert.True(_service.ExistsInPrivate("emp-1", "PRIVATE@EXAMPLE.COM"));
        Assert.False(_service.ExistsInPrivate("emp-2", "private@example.com"));
        Assert.False(_service.ExistsInPrivate("emp-1", "nonexistent@example.com"));
        Assert.False(_service.ExistsInPrivate("", "private@example.com"));
        Assert.False(_service.ExistsInPrivate("emp-1", ""));
    }

    [Fact]
    public void SavePublicContact_NullContact_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _service.SavePublicContact(null!));
    }

    [Fact]
    public void SavePrivateContact_Validation_ThrowsExpectedExceptions()
    {
        var contact = new ContactPerson { Id = "c-1", Name = "Test", Email = "test@example.com" };
        Assert.Throws<ArgumentNullException>(() => _service.SavePrivateContact("emp-1", null!));
        Assert.Throws<ArgumentException>(() => _service.SavePrivateContact("", contact));
        Assert.Throws<ArgumentException>(() => _service.SavePrivateContact("   ", contact));
    }
}
