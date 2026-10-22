using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates legacy Supplier.Info contact person fields into first-class Supplier.Contacts records
/// and ensures all suppliers with contacts have a designated primary contact.
/// </summary>
public class SupplierContactsMigrationStep : IMigrationStep
{
    private readonly SupplierRepository _supplierRepo;
    private readonly ILogger<SupplierContactsMigrationStep>? _logger;

    public string MigrationKey => "20260107_SupplierContactsMigration";
    public string Description => "Migrates legacy Supplier.Info contact person fields into Supplier.Contacts";
    public int Order => 80;

    public SupplierContactsMigrationStep(SupplierRepository supplierRepo, ILogger<SupplierContactsMigrationStep>? logger = null)
    {
        _supplierRepo = supplierRepo;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migrated = 0;
        foreach (var supplier in _supplierRepo.GetAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            bool modified = false;

            if (supplier.Contacts.Count == 0 && !string.IsNullOrWhiteSpace(supplier.ContactInfo.ContactPersonName))
            {
                supplier.Contacts.Add(new ContactPerson
                {
                    Prefix = supplier.ContactInfo.ContactPersonPrefix,
                    Name = supplier.ContactInfo.ContactPersonName,
                    Title = supplier.ContactInfo.ContactPersonTitle,
                    Email = supplier.ContactInfo.ContactPersonEmail,
                    Phone = supplier.ContactInfo.ContactPersonPhone,
                    IsPrimary = true
                });
                modified = true;
            }

            if (supplier.Contacts.Count > 0 && !supplier.Contacts.Any(c => c.IsPrimary))
            {
                supplier.Contacts[0].IsPrimary = true;
                modified = true;
            }

            if (modified)
            {
                _supplierRepo.Save(supplier);
                migrated++;
            }
        }

        if (migrated > 0)
        {
            _logger?.LogInformation("[SupplierContactsMigrationStep] Migrated contacts for {Count} supplier(s).", migrated);
        }

        return Task.CompletedTask;
    }
}
