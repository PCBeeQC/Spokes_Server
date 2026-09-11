using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services;
using System.Linq;

namespace Spokes_Server.Tests.Core.Extensions
{
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

            // Verify services
            Assert.Contains(services, d => d.ServiceType == typeof(ChatService) && d.Lifetime == ServiceLifetime.Singleton);
            Assert.Contains(services, d => d.ServiceType == typeof(BoardService) && d.Lifetime == ServiceLifetime.Singleton);

            // Verify scoped service
            Assert.Contains(services, d => d.ServiceType == typeof(ChatNotificationService) && d.Lifetime == ServiceLifetime.Scoped);
        }
    }
}


