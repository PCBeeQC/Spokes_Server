namespace Spokes_Server.Core.Services.Communication;

using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Domain service interface for managing public and private contacts.
/// </summary>
public interface IContactService
{
    bool ExistsInPublic(string email);
    bool ExistsInPrivate(string employeeId, string email);
    void SavePublicContact(ContactPerson contact);
    void SavePrivateContact(string employeeId, ContactPerson contact);
}
