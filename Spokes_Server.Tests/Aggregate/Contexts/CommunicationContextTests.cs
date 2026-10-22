namespace Spokes_Server.Tests.Aggregate.Contexts;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Xunit;

public class CommunicationContextTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CommunicationContext _context;
    private readonly CompanyProfileRepository _companyProfileRepo;

    public CommunicationContextTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<CommunicationContext>();
        _companyProfileRepo = _serviceProvider.GetRequiredService<CompanyProfileRepository>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Resolution_FromServiceProvider_ResolvesAndPopulatesAllRepositories()
    {
        Assert.NotNull(_context.ChatChannels);
        Assert.NotNull(_context.ChatCategories);
        Assert.NotNull(_context.ChatMessages);
        Assert.NotNull(_context.ChatReadStates);
        Assert.NotNull(_context.ClientStates);
        Assert.NotNull(_context.ReportedMessages);
        Assert.NotNull(_context.Albums);
        Assert.NotNull(_context.EmailFolders);
        Assert.NotNull(_context.EmailMessages);
        Assert.NotNull(_context.PublicContacts);
        Assert.NotNull(_context.PrivateContacts);
    }

    [Fact]
    public void Config_WhenProfilePresent_DelegatesToCompanyProfile()
    {
        var profile = _companyProfileRepo.GetOrCreate();
        profile.Chat.MaxChatCacheSizeMb = 42;
        profile.Push.VapidSubject = "mailto:custom@spokes.local";
        profile.EmailSettings.ImapHost = "imap.custom.local";
        _companyProfileRepo.Save(profile);

        Assert.Equal(42, _context.GetChatConfig().MaxChatCacheSizeMb);
        Assert.Equal(42, _context.ChatConfig.MaxChatCacheSizeMb);

        Assert.Equal("mailto:custom@spokes.local", _context.GetPushConfig().VapidSubject);
        Assert.Equal("mailto:custom@spokes.local", _context.PushConfig.VapidSubject);

        Assert.Equal("imap.custom.local", _context.GetEmailSettings().ImapHost);
        Assert.Equal("imap.custom.local", _context.EmailSettings.ImapHost);
    }

    [Fact]
    public void Initialize_CreatesDefaultsAndReconcilesChannels()
    {
        _context.Initialize();

        var general = _context.ChatChannels.GetById("general");
        Assert.NotNull(general);

        var categories = _context.ChatCategories.GetAll();
        Assert.NotEmpty(categories);
    }

    [Fact]
    public void Constructor_WhenCompanyProfileNull_ReturnsDefaultConfigs()
    {
        var manualContext = new CommunicationContext(
            _context.ChatChannels,
            _context.ChatCategories,
            _context.ChatMessages,
            _context.ChatReadStates,
            _context.ClientStates,
            _context.ReportedMessages,
            _context.Albums,
            _context.EmailFolders,
            _context.EmailMessages,
            _context.PublicContacts,
            _context.PrivateContacts,
            companyProfile: null);

        Assert.NotNull(manualContext.GetChatConfig());
        Assert.NotNull(manualContext.ChatConfig);
        Assert.NotNull(manualContext.GetPushConfig());
        Assert.NotNull(manualContext.PushConfig);
        Assert.NotNull(manualContext.GetEmailSettings());
        Assert.NotNull(manualContext.EmailSettings);
    }
}
