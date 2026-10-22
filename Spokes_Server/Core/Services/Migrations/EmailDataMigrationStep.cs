using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migration step that migrates email folders, messages, and bodies to employee-centric storage.
/// Self-contained IMigrationStep implementation.
/// </summary>
public class EmailDataMigrationStep : IMigrationStep
{
    private readonly CommunicationContext? _communication;
    private readonly string _dataPath;
    private readonly ILogger? _logger;

    public string MigrationKey => "20260101_EmailEmployeeCentric";
    public string Description => "Migrates email folders, messages, and bodies to employee-centric storage";
    public int Order => 10;

    [ActivatorUtilitiesConstructor]
    public EmailDataMigrationStep(CommunicationContext communication, IConfiguration config, ILogger<EmailDataMigrationStep>? logger = null)
    {
        _communication = communication;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    internal EmailDataMigrationStep(CommunicationContext? communication, string dataPath, ILogger? logger = null)
    {
        _communication = communication;
        _dataPath = dataPath;
        _logger = logger;
    }

    internal EmailDataMigrationStep(string dataPath, ILogger? logger = null)
        : this((CommunicationContext?)null, dataPath, logger)
    {
    }

    /// <summary>
    /// Protected parameterless constructor strictly reserved for mock proxy frameworks (e.g. Moq).
    /// </summary>
    protected EmailDataMigrationStep() : this((CommunicationContext?)null, "Data", null) { }

    public virtual Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        MigrateEmailData();
        _communication?.EmailFolders.LoadFromDisk();
        _communication?.EmailMessages.LoadFromDisk();
        return Task.CompletedTask;
    }

    public virtual void MigrateEmailData()
    {
        var dataPath = _dataPath;

        var oldFoldersPath = Path.Combine(dataPath, "EmailFolders");
        var oldMessagesPath = Path.Combine(dataPath, "EmailMessages");
        var oldBodiesPath = Path.Combine(dataPath, "Emails");

        // 1. Migrate Folders
        if (Directory.Exists(oldFoldersPath))
        {
            foreach (var file in Directory.GetFiles(oldFoldersPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<EmailFolder>(json);
                    if (item != null)
                    {
                        var newDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Folders");
                        Directory.CreateDirectory(newDir);

                        var newPath = Path.Combine(newDir, Path.GetFileName(file));
                        if (!File.Exists(newPath)) File.Move(file, newPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[Migration] Failed to migrate folder file {File}", file);
                }
            }
        }

        // 2. Migrate Messages & Bodies
        if (Directory.Exists(oldMessagesPath))
        {
            foreach (var file in Directory.GetFiles(oldMessagesPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<EmailMessage>(json);
                    if (item != null)
                    {
                        // Move Message JSON
                        var newMsgDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Messages");
                        Directory.CreateDirectory(newMsgDir);

                        var newMsgPath = Path.Combine(newMsgDir, Path.GetFileName(file));
                        if (!File.Exists(newMsgPath)) File.Move(file, newMsgPath);

                        // Move Body HTML
                        var oldBodyFile = Path.Combine(oldBodiesPath, $"{item.Id}.html");
                        if (File.Exists(oldBodyFile))
                        {
                            var newBodyDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Bodies");
                            Directory.CreateDirectory(newBodyDir);

                            var newBodyPath = Path.Combine(newBodyDir, $"{item.Id}.html");
                            if (!File.Exists(newBodyPath)) File.Move(oldBodyFile, newBodyPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[Migration] Failed to migrate message file {File}", file);
                }
            }
        }
    }
}
