using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class EmployeeRepository : JsonRepository<Employee>
{
    private sealed record EncryptedStringSlot(
        string FieldName,
        Func<Employee, string> Getter,
        Action<Employee, string> Setter);

    private static readonly EncryptedStringSlot[] EncryptedStringSlots =
    [
        new(nameof(Employee.SocialInsuranceNumber), e => e.SocialInsuranceNumber, (e, v) => e.SocialInsuranceNumber = v),
        new(nameof(Employee.PhoneNumber), e => e.PhoneNumber, (e, v) => e.PhoneNumber = v),
        new(nameof(Employee.Address), e => e.Address, (e, v) => e.Address = v),
        new(nameof(Employee.EmergencyContact), e => e.EmergencyContact, (e, v) => e.EmergencyContact = v),
        new(nameof(Employee.EmergencyContactPhone), e => e.EmergencyContactPhone, (e, v) => e.EmergencyContactPhone = v),
        new(nameof(Employee.PersonalEmail), e => e.PersonalEmail, (e, v) => e.PersonalEmail = v)
    ];

    private readonly EncryptedFieldCipherCache _cipherCache;

    public EmployeeRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null, null)
    {
    }

    public EmployeeRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmployeeRepository>? logger)
        : this(writer, config, logger, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmployeeRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        ILogger<EmployeeRepository>? logger = null,
        EncryptionService? encryptionService = null)
        // FIX: Tell the base class to ONLY look for "profile.json" files
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "profile.json", logger)
    {
        _cipherCache = new EncryptedFieldCipherCache(encryptionService ?? new EncryptionService(config));
    }

    protected override string GetFilePath(Employee item) =>
        Path.Combine(_basePath, item.Id, "profile.json");

    /// <summary>
    /// Gets all active, non-system, non-suspended, non-banned employees eligible for assignment and selection.
    /// </summary>
    public virtual List<Employee> GetActiveEmployees() => GetAll().Where(e => e.IsSelectable).ToList();

    public override void Save(Employee item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureLoaded();
        NormalizeInMemoryFields(item);
        _cache[item.Id] = item;
        QueueEncryptedWrite(item);
        RaiseOnSaved(item);
    }

    public override void LoadFromDisk()
    {
        lock (_loadLock)
        {
            if (!Directory.Exists(_basePath))
            {
                RawCache.Clear();
                _isLoaded = true;
                RaiseOnLoaded();
                return;
            }

            string[] files = [];
            try
            {
                files = Directory.GetFiles(_basePath, _searchPattern, SearchOption.AllDirectories);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "[EmployeeRepository] Failed to read files from {BasePath}", _basePath);
                return;
            }

            var itemsToResave = new List<Employee>();
            RawCache.Clear();
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var rootNode = JsonNode.Parse(json)?.AsObject();
                    if (rootNode == null) continue;

                    var employeeIdHint = rootNode["Id"]?.GetValue<string>() ?? file;
                    bool needsMigrationWrite = DecryptLoadedJson(rootNode, employeeIdHint);

                    var emp = rootNode.Deserialize<Employee>();
                    if (emp == null) continue;

                    if (!string.IsNullOrEmpty(emp.AvatarBase64))
                    {
                        try
                        {
                            if (DataUriHelper.TryParse(emp.AvatarBase64, out var bytes, out _))
                            {
                                var filePath = Path.Combine(_basePath, emp.Id, "avatar.png");
                                File.WriteAllBytes(filePath, bytes);
                                emp.AvatarFile = "avatar.png";
                                emp.AvatarBase64 = null;
                                needsMigrationWrite = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "[EmployeeRepository] Failed to migrate avatar for {EmployeeId}", emp.Id);
                        }
                    }

                    RawCache[emp.Id] = emp;
                    if (needsMigrationWrite)
                    {
                        itemsToResave.Add(emp);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmployeeRepository] Failed to deserialize {File}", file);
                }
            }

            _isLoaded = true;
            foreach (var emp in itemsToResave)
            {
                QueueEncryptedWrite(emp);
            }

            RaiseOnLoaded();
        }
    }

    private bool DecryptLoadedJson(JsonObject rootNode, string employeeId)
    {
        bool needsMigrationWrite = false;

        foreach (var slot in EncryptedStringSlots)
        {
            if (_cipherCache.DecryptStringNode(rootNode, slot.FieldName, CacheKey(employeeId, slot.FieldName)))
            {
                needsMigrationWrite = true;
            }
        }

        // DateOfBirth (stored encrypted as ISO DateOnly "O" string when encrypted, or DateOnly string in plaintext)
        if (_cipherCache.DecryptStringNode(rootNode, nameof(Employee.DateOfBirth), CacheKey(employeeId, nameof(Employee.DateOfBirth))))
        {
            needsMigrationWrite = true;
        }

        // HourlyRate (stored encrypted as string, or number in legacy plaintext)
        if (DecryptDecimalNode(rootNode, nameof(Employee.HourlyRate), employeeId, nameof(Employee.HourlyRate)))
        {
            needsMigrationWrite = true;
        }

        // Payroll.AnnualSalary
        if (rootNode[nameof(Employee.Payroll)] is JsonObject payrollObj &&
            DecryptDecimalNode(payrollObj, nameof(EmployeePayrollProfile.AnnualSalary), employeeId, "Payroll.AnnualSalary"))
        {
            needsMigrationWrite = true;
        }

        return needsMigrationWrite;
    }

    private bool DecryptDecimalNode(JsonObject container, string propertyName, string employeeId, string fieldKey)
    {
        var node = container[propertyName];
        if (node == null) return false;

        if (node is JsonValue jv && jv.TryGetValue<decimal>(out _))
        {
            // Legacy unencrypted numeric value on disk
            return true;
        }

        var raw = EncryptedFieldCipherCache.GetStringNodeValue(node);
        if (string.IsNullOrEmpty(raw)) return false;

        if (_cipherCache.EncryptionService.TryDecrypt(raw, out var decrypted) &&
            decimal.TryParse(decrypted, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDecimal))
        {
            container[propertyName] = parsedDecimal;
            if (raw.StartsWith(EncryptedFieldCipherCache.EncryptedV2Prefix, StringComparison.Ordinal))
            {
                _cipherCache.SetCached(CacheKey(employeeId, fieldKey), parsedDecimal.ToString(CultureInfo.InvariantCulture), raw);
                return false;
            }
            return true;
        }

        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var plainDecimal))
        {
            container[propertyName] = plainDecimal;
            return true;
        }

        return false;
    }

    private void NormalizeInMemoryFields(Employee item)
    {
        foreach (var slot in EncryptedStringSlots)
        {
            var normalized = _cipherCache.NormalizeInMemory(CacheKey(item.Id, slot.FieldName), slot.Getter(item));
            slot.Setter(item, normalized);
        }
    }

    private void QueueEncryptedWrite(Employee item)
    {
        var rootNode = JsonSerializer.SerializeToNode(item)?.AsObject() ?? new JsonObject();

        foreach (var slot in EncryptedStringSlots)
        {
            rootNode[slot.FieldName] = _cipherCache.EncryptForDisk(CacheKey(item.Id, slot.FieldName), slot.Getter(item));
        }

        if (item.DateOfBirth.HasValue)
        {
            var dobString = item.DateOfBirth.Value.ToString("O", CultureInfo.InvariantCulture);
            rootNode[nameof(Employee.DateOfBirth)] = _cipherCache.EncryptForDisk(CacheKey(item.Id, nameof(Employee.DateOfBirth)), dobString);
        }
        else
        {
            _cipherCache.Remove(CacheKey(item.Id, nameof(Employee.DateOfBirth)));
        }

        rootNode[nameof(Employee.HourlyRate)] = _cipherCache.EncryptForDisk(
            CacheKey(item.Id, nameof(Employee.HourlyRate)),
            item.HourlyRate.ToString(CultureInfo.InvariantCulture));

        if (item.Payroll != null && rootNode[nameof(Employee.Payroll)] is JsonObject payrollObj)
        {
            payrollObj[nameof(EmployeePayrollProfile.AnnualSalary)] = _cipherCache.EncryptForDisk(
                CacheKey(item.Id, "Payroll.AnnualSalary"),
                item.Payroll.AnnualSalary.ToString(CultureInfo.InvariantCulture));
        }

        var utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(rootNode, EncryptedFieldCipherCache.WriteIndentedOptions);
        _writer.QueueWriteBytes(GetFilePath(item), utf8Bytes);
    }

    private static string CacheKey(string employeeId, string field) => $"{employeeId}:{field}";
}
