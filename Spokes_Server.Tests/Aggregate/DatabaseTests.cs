using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using System.IO;

namespace Spokes_Server.Tests.Aggregate
{
    public class DatabaseTests : TestDataTestBase
    {
        private readonly ServiceProvider _serviceProvider;

        public DatabaseTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());

            // Register EncryptionService (required by ServerConfigRepository, normally in AddSpokesCoreServices)
            services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();

            // Register all repositories and Database
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
        }

        [Fact]
        public void Resolution_Succeeds()
        {
            var db = _serviceProvider.GetRequiredService<Database>();
            Assert.NotNull(db);
        }

        [Fact]
        public void Initialize_RunsWithoutErrors()
        {
            var db = _serviceProvider.GetRequiredService<Database>();
            db.Initialize();

            // Verify it ran without throwing by getting properties
            Assert.NotNull(db.ChatChannels);
            Assert.NotNull(db.Projects);
        }

        [Fact]
        public void ReloadAll_RunsWithoutErrors()
        {
            var db = _serviceProvider.GetRequiredService<Database>();
            db.Initialize(); // Create files first

            db.ReloadAll();

            Assert.NotNull(db.Employees);
            Assert.NotNull(db.Projects);
            Assert.NotNull(db.CompanyProfile);
        }
    }
}

