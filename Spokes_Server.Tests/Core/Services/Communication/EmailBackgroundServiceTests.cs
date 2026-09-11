using Spokes_Server.Core.Services.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Communication;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class EmailBackgroundServiceTests
    {
        private class TestableEmailBackgroundService : EmailBackgroundService
        {
            public List<Employee> EmployeesToReturn { get; set; } = new();
            private readonly CancellationTokenSource _stopAfterFirstPoll = new();
            public TestableEmailBackgroundService(IServiceProvider sp, ILogger<EmailBackgroundService> logger, EmailIdleService idle)
                : base(sp, logger, idle) { }

            protected override List<Employee> GetSyncEmployees(EmployeeRepository repo) => EmployeesToReturn;
            protected override Task WaitOnStartupAsync(CancellationToken ct) => Task.CompletedTask;
            protected override async Task WaitBeforeNextPollAsync(CancellationToken ct)
            {
                // Cancel after the first poll to prevent an infinite tight loop
                // that would consume all available memory by creating millions of DI scopes.
                _stopAfterFirstPoll.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        private (TestableEmailBackgroundService service,
                Mock<IServiceProvider> spMock,
                EmailIdleService idleService,
                Mock<EmailService> emailServiceMock,
                Mock<EmployeeRepository> empRepoMock,
                Mock<EmailFolderRepository> folderRepoMock,
                Mock<EmailMessageRepository> msgRepoMock) CreateService()
        {
            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var mockScope = new Mock<IServiceScope>();
            var mockServiceProvider = new Mock<IServiceProvider>();

            mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
            mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
            mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(mockScopeFactory.Object);

            var mockIdleLogger = new Mock<ILogger<EmailIdleService>>();
            var idleService = new EmailIdleService(mockScopeFactory.Object, mockIdleLogger.Object);

            var mockLogger = new Mock<ILogger<EmailBackgroundService>>();
            var mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns("Data");

            var mockEmailService = new Mock<EmailService>(null!, null!, null!, null!, null!, mockConfig.Object, null!, null!, null!);
            var mockEmpRepo = new Mock<EmployeeRepository>(null!, mockConfig.Object);
            var mockFolderRepo = new Mock<EmailFolderRepository>(null!, mockConfig.Object);
            var mockMsgRepo = new Mock<EmailMessageRepository>(null!, mockConfig.Object);

            mockServiceProvider.Setup(x => x.GetService(typeof(EmailService))).Returns(mockEmailService.Object);
            mockServiceProvider.Setup(x => x.GetService(typeof(EmployeeRepository))).Returns(mockEmpRepo.Object);
            mockServiceProvider.Setup(x => x.GetService(typeof(EmailFolderRepository))).Returns(mockFolderRepo.Object);
            mockServiceProvider.Setup(x => x.GetService(typeof(EmailMessageRepository))).Returns(mockMsgRepo.Object);
            mockServiceProvider.Setup(x => x.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))).Returns(mockConfig.Object);

            var service = new TestableEmailBackgroundService(mockServiceProvider.Object, mockLogger.Object, idleService);
            return (service, mockServiceProvider, idleService, mockEmailService, mockEmpRepo, mockFolderRepo, mockMsgRepo);
        }

        [Fact]
        public async Task StartAndStop_DoesNotThrow()
        {
            var (service, _, _, _, _, _, _) = CreateService();
            var cts = new CancellationTokenSource();
            await service.StartAsync(cts.Token);
            await service.StopAsync(CancellationToken.None);
            Assert.True(true);
        }

        [Fact]
        public async Task ExecuteAsync_SyncsEmployeesAndWatchesInbox()
        {
            var (service, _, _, emailServiceMock, empRepoMock, folderRepoMock, msgRepoMock) = CreateService();

            service.EmployeesToReturn = new List<Employee>
            {
                new Employee { Id = "emp1", Email = "test@test.com", EncryptedEmailPassword = "pass", FirstName = "Test", LastName = "User" }
            };

            var folders = new List<EmailFolder>
            {
                new EmailFolder { EmployeeId = "emp1", Path = "Inbox", IsInbox = true, UnreadCount = 5 }
            };
            folderRepoMock.Setup(r => r.GetByEmployee("emp1")).Returns(folders);
            msgRepoMock.Setup(r => r.GetUnreadCount("emp1", "Inbox")).Returns(5);

            // Use a short timeout to let the background loop run once
            var cts = new CancellationTokenSource();
            var task = service.StartAsync(cts.Token);

            await Task.Delay(150); // Give it a moment to run

            await service.StopAsync(CancellationToken.None);

            emailServiceMock.Verify(s => s.SyncEmployeeAsync("emp1"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task OnIdleMessageReceived_SyncsSingleFolder()
        {
            var (service, _, idleService, emailServiceMock, _, _, _) = CreateService();

            // Trigger the IDLE service event manually via reflection
            var field = typeof(EmailIdleService).GetField("MessageReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var del = (Action<string, string, MailKit.UniqueId>?)field?.GetValue(idleService);
            del?.Invoke("emp2", "Inbox", MailKit.UniqueId.MinValue);

            // Give the Task.Run loop time to execute
            await Task.Delay(150);

            emailServiceMock.Verify(s => s.SyncSingleFolderAsync("emp2", "Inbox"), Times.AtLeastOnce());
        }

        [Fact]
        public async Task OnIdleFlagsChanged_SyncsSingleFolder()
        {
            var (service, _, idleService, emailServiceMock, _, _, _) = CreateService();

            // Trigger the IDLE service event manually via reflection
            var field = typeof(EmailIdleService).GetField("MessageFlagsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var del = (Action<string, string, MailKit.UniqueId>?)field?.GetValue(idleService);
            del?.Invoke("emp3", "Sent", MailKit.UniqueId.MinValue);

            // Give the Task.Run loop time to execute
            await Task.Delay(150);

            emailServiceMock.Verify(s => s.SyncSingleFolderAsync("emp3", "Sent"), Times.AtLeastOnce());
        }
    }
}
