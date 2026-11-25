using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailFolderRepository : JsonRepository<EmailFolder>
{
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailFolderRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailFolderRepository>? logger)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "EmailAccounts"), "EmailFolders.json", logger)
    {
    }

    protected override string GetFilePath(EmailFolder item) =>
        Path.Combine(_basePath, item.AccountId, "Folders", $"{item.Id}.json");

    public override void LoadFromDisk()
    {
        lock (_loadLock)
        {
            if (!Directory.Exists(_basePath))
            {
                RawCache.Clear();
                _isLoaded = true;
                return;
            }

            var tempFolders = new List<EmailFolder>();
            var accountDirs = Directory.GetDirectories(_basePath);
            foreach (var accountDir in accountDirs)
            {
                var folderPath = Path.Combine(accountDir, "Folders");
                if (!Directory.Exists(folderPath)) continue;

                var files = Directory.GetFiles(folderPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var item = JsonSerializer.Deserialize<EmailFolder>(json);
                        if (item != null) tempFolders.Add(item);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[EmailFolderRepo] Error loading folder file {File}", file);
                    }
                }
            }

            RawCache.Clear();
            foreach (var folder in tempFolders)
            {
                RawCache[folder.Id] = folder;
            }
            _isLoaded = true;
        }
    }

    public virtual List<EmailFolder> GetByAccount(string accountId) =>
        GetAll().Where(f => string.Equals(f.AccountId, accountId, StringComparison.OrdinalIgnoreCase)).ToList();

    public virtual void DeleteByAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        foreach (var folder in GetByAccount(accountId))
        {
            Delete(folder.Id);
        }
    }
}
