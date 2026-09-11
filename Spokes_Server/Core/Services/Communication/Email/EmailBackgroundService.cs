using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailBackgroundService> _logger;
    private readonly EmailIdleService _idleService;

    // Track which employees already have IDLE running
    private readonly HashSet<string> _idleStarted = new();

    public EmailBackgroundService(IServiceProvider serviceProvider, ILogger<EmailBackgroundService> logger, EmailIdleService idleService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _idleService = idleService;

        // Subscribe to IDLE events — runs server-side, independent of any UI component
        _idleService.MessageReceived += OnIdleMessageReceived;
        _idleService.MessageFlagsChanged += OnIdleFlagsChanged;
    }

    private void OnIdleMessageReceived(string employeeId, string folderPath, MailKit.UniqueId uid)
    {
        Console.WriteLine($"[EmailBG] IDLE detected new message in {folderPath} for {employeeId}. Syncing...");
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
                await emailService.SyncSingleFolderAsync(employeeId, folderPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailBG] IDLE sync failed: {ex.Message}");
            }
        });
    }

    private void OnIdleFlagsChanged(string employeeId, string folderPath, MailKit.UniqueId uid)
    {
        Console.WriteLine($"[EmailBG] IDLE detected flags change in {folderPath} for {employeeId}. Syncing...");
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();
                await emailService.SyncSingleFolderAsync(employeeId, folderPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailBG] IDLE flag sync failed: {ex.Message}");
            }
        });
    }

    protected virtual List<Employee> GetSyncEmployees(EmployeeRepository repo)
    {
        return repo.GetAll()
            .Where(e => !string.IsNullOrEmpty(e.Email) && !string.IsNullOrEmpty(e.EncryptedEmailPassword))
            .ToList();
    }

    protected virtual Task WaitBeforeNextPollAsync(CancellationToken stoppingToken)
    {
        return Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
    }

    protected virtual Task WaitOnStartupAsync(CancellationToken stoppingToken)
    {
        return Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email Background Service started.");

        try
        {
            await WaitOnStartupAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var employeesRepo = scope.ServiceProvider.GetRequiredService<EmployeeRepository>();
                    var foldersRepo = scope.ServiceProvider.GetRequiredService<EmailFolderRepository>();
                    var messagesRepo = scope.ServiceProvider.GetRequiredService<EmailMessageRepository>();
                    var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

                    var employees = GetSyncEmployees(employeesRepo);

                    foreach (var employee in employees)
                    {
                        try
                        {
                            await emailService.SyncEmployeeAsync(employee.Id);

                            // Reconcile unread counts — catches any drift between index and folder entity
                            var folders = foldersRepo.GetByEmployee(employee.Id);
                            foreach (var folder in folders)
                            {
                                var indexCount = messagesRepo.GetUnreadCount(employee.Id, folder.Path);
                                if (folder.UnreadCount != indexCount)
                                {
                                    folder.UnreadCount = indexCount;
                                    foldersRepo.Save(folder);
                                }
                            }

                            // Start IDLE on the inbox (if not already running)
                            if (!_idleStarted.Contains(employee.Id))
                            {
                                var inbox = folders.FirstOrDefault(f => f.IsInbox);
                                if (inbox != null)
                                {
                                    _idleService.WatchFolder(employee.Id, inbox.Path);
                                    _idleStarted.Add(employee.Id);
                                    Console.WriteLine($"[EmailBG] Started IDLE watch for {employee.FullName} on {inbox.Path}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error syncing email for employee {employee.FullName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Email Background Loop");
            }

            try
            {
                await WaitBeforeNextPollAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}



