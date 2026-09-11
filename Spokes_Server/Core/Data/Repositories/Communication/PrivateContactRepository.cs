using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Repository for managing private contact persons per employee.
/// NOTE: This class intentionally does not inherit from JsonRepository&lt;ContactPerson&gt; because contact persons
/// are partitioned hierarchically on disk by employee ID (Data/Employees/{EmployeeId}/Contacts/{ContactId}.json).
/// Inheriting from JsonRepository would require a flat directory structure, violating the user-private scoping of contact files,
/// or require exposing multi-tenant methods (e.g. Save, Delete, GetById) that lack the necessary employee context.
/// </summary>
public class PrivateContactRepository
{
    private readonly DiskPersistenceService _writer;
    private readonly string _baseDataPath;

    private readonly ILogger<PrivateContactRepository> _logger;

    // Cache structure: Dictionary<EmployeeId, Dictionary<ContactId, ContactPerson>>
    private readonly Dictionary<string, Dictionary<string, ContactPerson>> _employeeCaches = new();
    private readonly object _lock = new();

    public PrivateContactRepository(DiskPersistenceService writer, IConfiguration config, ILogger<PrivateContactRepository> logger)
    {
        _writer = writer;
        _baseDataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public void LoadFromDisk()
    {
        lock (_lock)
        {
            _employeeCaches.Clear();
            var employeesDir = Path.Combine(_baseDataPath, "Employees");
            if (!Directory.Exists(employeesDir)) return;

            foreach (var employeeDir in Directory.GetDirectories(employeesDir))
            {
                var employeeId = Path.GetFileName(employeeDir);
                var contactsDir = Path.Combine(employeeDir, "Contacts");

                if (Directory.Exists(contactsDir))
                {
                    var cache = new Dictionary<string, ContactPerson>();
                    foreach (var file in Directory.GetFiles(contactsDir, "*.json"))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            var item = System.Text.Json.JsonSerializer.Deserialize<ContactPerson>(json);
                            if (item != null)
                            {
                                cache[item.Id] = item;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error loading contact {File}", file);
                        }
                    }
                    if (cache.Count > 0)
                    {
                        _employeeCaches[employeeId] = cache;
                    }
                }
            }
        }
    }

    public List<ContactPerson> GetAllForEmployee(string employeeId)
    {
        lock (_lock)
        {
            if (_employeeCaches.TryGetValue(employeeId, out var cache))
            {
                return cache.Values.ToList();
            }
            return new List<ContactPerson>();
        }
    }

    public ContactPerson? GetById(string employeeId, string contactId)
    {
        lock (_lock)
        {
            if (_employeeCaches.TryGetValue(employeeId, out var cache))
            {
                if (cache.TryGetValue(contactId, out var item))
                {
                    return item;
                }
            }
            return null;
        }
    }

    public void Save(string employeeId, ContactPerson item)
    {
        lock (_lock)
        {
            if (!_employeeCaches.TryGetValue(employeeId, out var cache))
            {
                cache = new Dictionary<string, ContactPerson>();
                _employeeCaches[employeeId] = cache;
            }

            cache[item.Id] = item;

            var contactsDir = Path.Combine(_baseDataPath, "Employees", employeeId, "Contacts");
            if (!Directory.Exists(contactsDir))
            {
                Directory.CreateDirectory(contactsDir);
            }

            var filePath = Path.Combine(contactsDir, $"{item.Id}.json");
            _writer.QueueWrite(filePath, item);
        }
    }

    public void Delete(string employeeId, string contactId)
    {
        lock (_lock)
        {
            if (_employeeCaches.TryGetValue(employeeId, out var cache))
            {
                if (cache.ContainsKey(contactId))
                {
                    cache.Remove(contactId);
                    var filePath = Path.Combine(_baseDataPath, "Employees", employeeId, "Contacts", $"{contactId}.json");
                    _writer.QueueDelete(filePath);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _employeeCaches.Clear();
        }
    }
}


