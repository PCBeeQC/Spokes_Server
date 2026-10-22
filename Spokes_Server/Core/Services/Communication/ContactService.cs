using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication;

/// <summary>
/// Domain service for managing public and private contacts.
/// </summary>
public class ContactService : IContactService
{
    private readonly PublicContactRepository _publicContacts;
    private readonly PrivateContactRepository _privateContacts;
    private readonly ILogger<ContactService>? _logger;

    public ContactService(
        PublicContactRepository publicContacts,
        PrivateContactRepository privateContacts,
        ILogger<ContactService>? logger = null)
    {
        _publicContacts = publicContacts;
        _privateContacts = privateContacts;
        _logger = logger;
    }

    public virtual bool ExistsInPublic(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        return _publicContacts.GetAll().Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    public virtual bool ExistsInPrivate(string employeeId, string email)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(email)) return false;
        return _privateContacts.GetAllForEmployee(employeeId).Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    public virtual void SavePublicContact(ContactPerson contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        _publicContacts.Save(contact);
    }

    public virtual void SavePrivateContact(string employeeId, ContactPerson contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (string.IsNullOrWhiteSpace(employeeId))
            throw new ArgumentException("Employee ID is required.", nameof(employeeId));

        _privateContacts.Save(employeeId, contact);
    }
}
