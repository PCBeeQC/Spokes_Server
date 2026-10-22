using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Maintenance;

namespace Spokes_Server.Tests.Core.Extensions;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSpokesDatabase_RegistersRequiredServices()
    {
        var services = new ServiceCollection();

        // Add required dependencies that the repositories might need during instantiation
        var mockConfig = new Mock<IConfiguration>();
        services.AddSingleton(mockConfig.Object);

        // Call the extension method
        services.AddSpokesDatabase();

        // Verify DiskPersistenceService is registered as both Singleton and HostedService
        Assert.Contains(services, d => d.ServiceType == typeof(DiskPersistenceService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory != null);

        // Verify core components
        Assert.Contains(services, d => d.ServiceType == typeof(Database) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(SequenceService) && d.Lifetime == ServiceLifetime.Singleton);

        // Verify a sample of repositories
        Assert.Contains(services, d => d.ServiceType == typeof(ProjectRepository) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(EmployeeRepository) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ChatChannelRepository) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(EmailFolderRepository) && d.Lifetime == ServiceLifetime.Singleton);

        // Verify aggregate contexts
        Assert.Contains(services, d => d.ServiceType == typeof(CoreContext) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ProjectsContext) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(HrContext) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(AccountingContext) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(CommunicationContext) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSpokesCoreServices_RegistersRequiredServices()
    {
        var services = new ServiceCollection();

        // Add dependencies needed by core services
        var mockConfig = new Mock<IConfiguration>();
        services.AddSingleton(mockConfig.Object);
        services.AddLogging();
        services.AddSpokesConfiguration(mockConfig.Object);
        services.AddSpokesDatabase();

        // Call the extension method
        services.AddSpokesCoreServices();

        // Verify high-level domain services
        Assert.Contains(services, d => d.ServiceType == typeof(ChatService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(BoardService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ChatNotificationService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(ServerEscrowService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(TimesheetTimerService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(CasdoorProvisioningService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(BackupService) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSpokesWebUi_RegistersCircuitHandlers()
    {
        var services = new ServiceCollection();
        var mockWebHost = new Mock<IWebHostBuilder>();

        services.AddSpokesWebUi(mockWebHost.Object);

        // Verify presentation circuit context and circuit handlers
        Assert.Contains(services, d => d.ServiceType == typeof(Spokes_Server.Core.Services.Communication.Presence.UserCircuitContext) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler) && d.ImplementationType == typeof(Spokes_Server.Core.Services.Communication.Presence.PresenceCircuitHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler) && d.ImplementationType == typeof(Spokes_Server.Core.Services.Communication.Voice.VoiceCircuitHandler));
    }

    [Fact]
    public void AddSpokesWebUi_RegistersOptionSetups_ConfiguringDefaultsAndDynamicLimits()
    {
        var services = new ServiceCollection();
        var mockWebHost = new Mock<IWebHostBuilder>();

        services.AddSpokesWebUi(mockWebHost.Object);

        using var sp = services.BuildServiceProvider();
        var kestrelSetup = new KestrelServerOptionsSetup(sp);
        var kestrelOptions = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions();
        kestrelSetup.Configure(kestrelOptions);
        Assert.Equal(WebUiExtensions.DefaultMaxFileUploadSizeBytes, kestrelOptions.Limits.MaxRequestBodySize);

        var formSetup = new FormOptionsSetup(sp);
        var formOptions = new Microsoft.AspNetCore.Http.Features.FormOptions();
        formSetup.Configure(formOptions);
        Assert.Equal(WebUiExtensions.DefaultMaxFileUploadSizeBytes, formOptions.MultipartBodyLengthLimit);

        var hubSetup = new HubOptionsSetup(sp);
        var hubOptions = new Microsoft.AspNetCore.SignalR.HubOptions();
        hubSetup.Configure(hubOptions);
        Assert.Equal(WebUiExtensions.DefaultHubMessageSizeLimitBytes, hubOptions.MaximumReceiveMessageSize);
        Assert.Equal(64 * 1024 * 1024, hubOptions.MaximumReceiveMessageSize);

        var circuitSetup = new CircuitOptionsSetup(sp);
        var circuitOptions = new Microsoft.AspNetCore.Components.Server.CircuitOptions();
        circuitSetup.Configure(circuitOptions);
        Assert.Equal(TimeSpan.FromHours(2), circuitOptions.DisconnectedCircuitRetentionPeriod);
        Assert.Equal(100, circuitOptions.DisconnectedCircuitMaxRetained);
        Assert.Equal(TimeSpan.FromSeconds(60), circuitOptions.JSInteropDefaultCallTimeout);
    }

    [Fact]
    public void AddSpokesWebUi_HubOptions_RemainsDecoupledFromCustomFileUploadSize()
    {
        var services = new ServiceCollection();
        var mockWebHost = new Mock<IWebHostBuilder>();

        const long customLargeUploadLimit = 1024L * 1024 * 1024; // 1 GB
        var mockCompanyProfileRepo = new Mock<CompanyProfileRepository>(
            new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object),
            new Mock<IConfiguration>().Object,
            null);
        mockCompanyProfileRepo.Setup(r => r.Get()).Returns(new CompanyProfile
        {
            Storage = new StorageConfig { MaxFileUploadSizeBytes = customLargeUploadLimit }
        });
        services.AddSingleton(mockCompanyProfileRepo.Object);

        services.AddSpokesWebUi(mockWebHost.Object);

        using var sp = services.BuildServiceProvider();

        var kestrelSetup = new KestrelServerOptionsSetup(sp);
        var kestrelOptions = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions();
        kestrelSetup.Configure(kestrelOptions);
        Assert.Equal(customLargeUploadLimit, kestrelOptions.Limits.MaxRequestBodySize);

        var formSetup = new FormOptionsSetup(sp);
        var formOptions = new Microsoft.AspNetCore.Http.Features.FormOptions();
        formSetup.Configure(formOptions);
        Assert.Equal(customLargeUploadLimit, formOptions.MultipartBodyLengthLimit);

        var hubSetup = new HubOptionsSetup(sp);
        var hubOptions = new Microsoft.AspNetCore.SignalR.HubOptions();
        hubSetup.Configure(hubOptions);
        // Hub options must remain strictly decoupled at 64MB, never ballooning with file upload limits
        Assert.Equal(WebUiExtensions.DefaultHubMessageSizeLimitBytes, hubOptions.MaximumReceiveMessageSize);
        Assert.Equal(64 * 1024 * 1024, hubOptions.MaximumReceiveMessageSize);
    }

    [Fact]
    public void AddSpokesDatabase_IsSelfContained_CanResolveRepositoriesWithoutCoreServices()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        services.AddSingleton(mockConfig.Object);
        services.AddLogging();

        services.AddSpokesDatabase();

        using var sp = services.BuildServiceProvider();
        var encryptionService = sp.GetRequiredService<EncryptionService>();
        var serverConfigRepo = sp.GetRequiredService<ServerConfigRepository>();
        var companyProfileRepo = sp.GetRequiredService<CompanyProfileRepository>();

        Assert.NotNull(encryptionService);
        Assert.NotNull(serverConfigRepo);
        Assert.NotNull(companyProfileRepo);
    }
}
