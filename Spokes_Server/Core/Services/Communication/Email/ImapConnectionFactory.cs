using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class ImapConnectionFactory : IImapConnectionFactory
{
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmployeeRepository _employees;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<ImapConnectionFactory>? _logger;

    public ImapConnectionFactory(
        CompanyProfileRepository companyProfile,
        EmployeeRepository employees,
        EncryptionService encryptionService,
        ILogger<ImapConnectionFactory>? logger = null)
    {
        _companyProfile = companyProfile;
        _employees = employees;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    public async Task<ImapConnectionResult> ConnectAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(employeeId))
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Employee ID is missing");

        var employee = _employees.GetById(employeeId);
        if (employee == null)
        {
            _logger?.LogWarning("[ImapConnectionFactory] Employee {EmployeeId} not found", employeeId);
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, $"Employee {employeeId} not found");
        }

        return await ConnectAsync(employee, cancellationToken);
    }

    public async Task<ImapConnectionResult> ConnectAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employee.Email) || string.IsNullOrEmpty(employee.EncryptedEmailPassword))
        {
            _logger?.LogWarning("[ImapConnectionFactory] Employee {EmployeeId} has missing email credentials", employee.Id);
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Missing email credentials");
        }

        var settings = _companyProfile.Get()?.EmailSettings;
        if (settings == null || string.IsNullOrWhiteSpace(settings.ImapHost))
        {
            _logger?.LogWarning("[ImapConnectionFactory] IMAP host is not configured");
            return new ImapConnectionResult(null, ImapConnectionStatus.ServerNotConfigured, "IMAP server not configured");
        }

        string password;
        try
        {
            password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            if (string.IsNullOrEmpty(password))
            {
                _logger?.LogWarning("[ImapConnectionFactory] Decrypted email password for employee {EmployeeId} is empty", employee.Id);
                return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Decrypted password is empty");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ImapConnectionFactory] Failed to decrypt email password for employee {EmployeeId}", employee.Id);
            return new ImapConnectionResult(null, ImapConnectionStatus.DecryptionError, ex.Message);
        }

        var client = new ImapClient();
        try
        {
            var sslOptions = settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, sslOptions, cancellationToken);
            await client.AuthenticateAsync(employee.Email, password, cancellationToken);
            return new ImapConnectionResult(client, ImapConnectionStatus.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            _logger?.LogWarning(ex, "[ImapConnectionFactory] Failed to connect or authenticate IMAP client for {Email}", employee.Email);
            return new ImapConnectionResult(null, ImapConnectionStatus.ConnectionFailed, ex.Message);
        }
    }

    public async Task<ImapClient?> CreateAuthenticatedClientAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ConnectAsync(employeeId, cancellationToken);
            return result.Client;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ImapClient?> CreateAuthenticatedClientAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ConnectAsync(employee, cancellationToken);
            return result.Client;
        }
        catch
        {
            return null;
        }
    }
}
