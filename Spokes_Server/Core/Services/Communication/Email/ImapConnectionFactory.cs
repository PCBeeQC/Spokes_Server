using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class ImapConnectionFactory : IImapConnectionFactory
{
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailAccountRepository? _accounts;
    private readonly EncryptionService _encryptionService;
    private readonly ILogger<ImapConnectionFactory>? _logger;
    private readonly EmployeeRepository? _employees;

    public ImapConnectionFactory(
        CompanyProfileRepository companyProfile,
        EncryptionService encryptionService,
        EmailAccountRepository? accounts = null,
        ILogger<ImapConnectionFactory>? logger = null,
        EmployeeRepository? employees = null)
    {
        _companyProfile = companyProfile;
        _accounts = accounts;
        _encryptionService = encryptionService;
        _logger = logger;
        _employees = employees;
    }

    public async Task<ImapConnectionResult> ConnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(accountId))
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Account ID is missing");

        var account = _accounts?.GetById(accountId);
        if (account == null)
        {
            _logger?.LogWarning("[ImapConnectionFactory] EmailAccount {AccountId} not found", accountId);
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, $"Email account {accountId} not found");
        }

        return await ConnectAsync(account, cancellationToken);
    }

    public async Task<ImapConnectionResult> ConnectAsync(EmailAccount account, CancellationToken cancellationToken = default)
    {
        var username = !string.IsNullOrWhiteSpace(account.Username) ? account.Username : account.EmailAddress;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(account.EncryptedPassword))
        {
            _logger?.LogWarning("[ImapConnectionFactory] EmailAccount {AccountId} has missing credentials", account.Id);
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Missing email credentials");
        }

        var (host, port, useSsl, usedConfiguredServer) = ResolveServerEndpointDetails(account);
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger?.LogWarning("[ImapConnectionFactory] IMAP host is not configured for account {AccountId}", account.Id);
            return new ImapConnectionResult(null, ImapConnectionStatus.ServerNotConfigured, "IMAP server not configured");
        }

        string password;
        try
        {
            password = _encryptionService.Decrypt(account.EncryptedPassword);
            if (string.IsNullOrEmpty(password))
            {
                _logger?.LogWarning("[ImapConnectionFactory] Decrypted email password for account {AccountId} is empty", account.Id);
                return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Decrypted password is empty");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ImapConnectionFactory] Failed to decrypt email password for account {AccountId}", account.Id);
            return new ImapConnectionResult(null, ImapConnectionStatus.DecryptionError, ex.Message);
        }

        bool isTrustedServer = account.IsServerManaged || usedConfiguredServer;
        return await ConnectAndAuthenticateCoreAsync(
            host,
            port,
            useSsl,
            username,
            password,
            allowPlaintext: isTrustedServer,
            requireSafeExternalHost: !isTrustedServer,
            cancellationToken);
    }

    public Task<ImapConnectionResult> TestConnectionAsync(
        string host,
        int port,
        bool useSsl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Secure by default for raw ad-hoc connection tests: enforce TLS and DNS-pinned SSRF checks.
        return ConnectAndAuthenticateCoreAsync(
            host,
            port,
            useSsl,
            username,
            password,
            allowPlaintext: false,
            requireSafeExternalHost: true,
            cancellationToken);
    }

    private async Task<ImapConnectionResult> ConnectAndAuthenticateCoreAsync(
        string host,
        int port,
        bool useSsl,
        string username,
        string password,
        bool allowPlaintext,
        bool requireSafeExternalHost,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new ImapConnectionResult(null, ImapConnectionStatus.ServerNotConfigured, "IMAP host is required");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new ImapConnectionResult(null, ImapConnectionStatus.MissingCredentials, "Username and password are required");

        var client = new ImapClient();
        try
        {
            var effectivePort = MailTransportSecurityHelper.NormalizeImapPort(port, useSsl);
            var sslOptions = MailTransportSecurityHelper.ResolveImapTlsOptions(effectivePort, useSsl, allowPlaintext);

            await MailTransportSecurityHelper.ConnectMailServiceAsync(
                client,
                host,
                effectivePort,
                sslOptions,
                requireSafeExternalHost,
                cancellationToken);

            await client.AuthenticateAsync(username, password, cancellationToken);
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
            _logger?.LogWarning(ex, "[ImapConnectionFactory] Failed to connect or authenticate IMAP client for {Username}@{Host}", username, host);
            return new ImapConnectionResult(null, ImapConnectionStatus.ConnectionFailed, ex.Message);
        }
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(
        EmailAccount account,
        string? plainPasswordOverride = null,
        CancellationToken cancellationToken = default)
    {
        Employee? inferredCaller = null;
        if (!account.IsServerManaged && !string.IsNullOrWhiteSpace(account.OwnerEmployeeId))
        {
            inferredCaller = _employees?.GetById(account.OwnerEmployeeId);
        }

        return TestConnectionInternalAsync(account, plainPasswordOverride, inferredCaller, explicitCallerProvided: false, cancellationToken);
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(
        EmailAccount account,
        string? plainPasswordOverride,
        Employee? caller,
        CancellationToken cancellationToken = default)
    {
        return TestConnectionInternalAsync(account, plainPasswordOverride, caller, explicitCallerProvided: true, cancellationToken);
    }

    private async Task<(bool Success, string Message)> TestConnectionInternalAsync(
        EmailAccount account,
        string? plainPasswordOverride,
        Employee? caller,
        bool explicitCallerProvided,
        CancellationToken cancellationToken)
    {
        bool callerIsAdmin;
        if (explicitCallerProvided)
        {
            if (caller == null || !caller.IsActive || caller.IsBanned || caller.IsSuspended)
            {
                return (false, MailTransportSecurityHelper.SanitizedConnectionErrorMessage);
            }

            callerIsAdmin = caller.IsAdmin || caller.HasPermission(AppPermissions.Admin.ManageSettings);
            if (account.IsServerManaged && !callerIsAdmin)
            {
                return (false, "Not authorized to test server-managed email accounts.");
            }
        }
        else if (caller != null)
        {
            if (!caller.IsActive || caller.IsBanned || caller.IsSuspended)
            {
                return (false, MailTransportSecurityHelper.SanitizedConnectionErrorMessage);
            }

            callerIsAdmin = caller.IsAdmin || caller.HasPermission(AppPermissions.Admin.ManageSettings);
        }
        else
        {
            // When no caller was supplied and OwnerEmployeeId is absent, only server-managed contexts get verbose admin errors.
            callerIsAdmin = account.IsServerManaged;
        }

        var username = !string.IsNullOrWhiteSpace(account.Username) ? account.Username : account.EmailAddress;
        var (host, port, useSsl, usedConfiguredServer) = ResolveServerEndpointDetails(account);
        if (string.IsNullOrWhiteSpace(host))
            return (false, "IMAP host is not configured.");

        var password = plainPasswordOverride;
        if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(account.EncryptedPassword))
        {
            try
            {
                password = _encryptionService.Decrypt(account.EncryptedPassword);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ImapConnectionFactory] Failed to decrypt saved password during TestConnectionAsync for account {AccountId}", account.Id);
                return (false, callerIsAdmin ? $"Failed to decrypt saved password: {ex.Message}" : MailTransportSecurityHelper.SanitizedConnectionErrorMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "Username and password are required.");

        bool isTrustedServer = account.IsServerManaged || usedConfiguredServer;
        using var result = await ConnectAndAuthenticateCoreAsync(
            host,
            port,
            useSsl,
            username,
            password,
            allowPlaintext: isTrustedServer,
            requireSafeExternalHost: !isTrustedServer,
            cancellationToken);

        if (result.IsSuccess)
        {
            try
            {
                await result.Client!.DisconnectAsync(true, cancellationToken);
            }
            catch
            {
                // Ignore disconnect errors after successful auth
            }
            return (true, "Connection successful!");
        }

        var errorMessage = callerIsAdmin
            ? (result.ErrorMessage ?? "Connection failed.")
            : MailTransportSecurityHelper.SanitizedConnectionErrorMessage;
        return (false, errorMessage);
    }

    public async Task<string?> TryDetectSenderDisplayNameAsync(EmailAccount account, string? plainPasswordOverride = null, CancellationToken cancellationToken = default)
    {
        var username = !string.IsNullOrWhiteSpace(account.Username) ? account.Username : account.EmailAddress;
        var (host, port, useSsl, usedConfiguredServer) = ResolveServerEndpointDetails(account);
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var password = plainPasswordOverride;
        if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(account.EncryptedPassword))
        {
            try
            {
                password = _encryptionService.Decrypt(account.EncryptedPassword);
            }
            catch
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        try
        {
            bool isTrustedServer = account.IsServerManaged || usedConfiguredServer;
            using var result = await ConnectAndAuthenticateCoreAsync(
                host,
                port,
                useSsl,
                username,
                password,
                allowPlaintext: isTrustedServer,
                requireSafeExternalHost: !isTrustedServer,
                cancellationToken);
            if (!result.IsSuccess || result.Client == null)
                return null;

            var detected = await TryDetectSenderDisplayNameFromClientAsync(result.Client, account.EmailAddress, cancellationToken);
            try
            {
                await result.Client.DisconnectAsync(true, cancellationToken);
            }
            catch
            {
                // Ignore disconnect errors
            }
            return detected;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ImapConnectionFactory] Could not auto-detect sender display name for {Email}", account.EmailAddress);
            return null;
        }
    }

    private static async Task<string?> TryDetectSenderDisplayNameFromClientAsync(ImapClient client, string? emailAddress, CancellationToken cancellationToken)
    {
        IMailFolder? sentFolder = null;
        try
        {
            if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || client.Capabilities.HasFlag(ImapCapabilities.XList))
            {
                sentFolder = client.GetFolder(SpecialFolder.Sent);
            }
        }
        catch
        {
            sentFolder = null;
        }

        if (sentFolder == null && client.PersonalNamespaces.Count > 0)
        {
            try
            {
                var personalFolders = await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: cancellationToken);
                sentFolder = personalFolders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Sent))
                    ?? personalFolders.FirstOrDefault(f =>
                        string.Equals(f.Name, "Sent", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Name, "Sent Mail", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Name, "Sent Items", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.FullName, "[Gmail]/Sent Mail", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                sentFolder = null;
            }
        }

        if (sentFolder == null)
            return null;

        await sentFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        if (sentFolder.Count <= 0)
            return null;

        int startIndex = Math.Max(0, sentFolder.Count - 10);
        var summaries = await sentFolder.FetchAsync(startIndex, -1, MessageSummaryItems.Envelope, cancellationToken);
        var cleanEmail = emailAddress?.Trim();

        foreach (var summary in summaries.Reverse())
        {
            var fromMailboxes = summary.Envelope?.From?.Mailboxes;
            if (fromMailboxes == null) continue;

            foreach (var mb in fromMailboxes)
            {
                if (string.IsNullOrWhiteSpace(mb.Name)) continue;
                var candidateName = mb.Name.Trim();
                if (string.Equals(candidateName, mb.Address?.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(cleanEmail) ||
                    string.Equals(mb.Address?.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return candidateName;
                }
            }
        }

        return null;
    }

    public (string Host, int Port, bool UseSsl) ResolveServerEndpoint(EmailAccount account)
    {
        var (host, port, useSsl, _) = ResolveServerEndpointDetails(account);
        return (host, port, useSsl);
    }

    private (string Host, int Port, bool UseSsl, bool UsedConfiguredServer) ResolveServerEndpointDetails(EmailAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.IncomingServerConfigId))
        {
            var settings = _companyProfile.Get()?.EmailSettings;
            var preset = settings?.IncomingServers?.FirstOrDefault(s => string.Equals(s.Id, account.IncomingServerConfigId, StringComparison.OrdinalIgnoreCase));
            if (preset != null && !string.IsNullOrWhiteSpace(preset.Host))
            {
                return (preset.Host, MailTransportSecurityHelper.NormalizeImapPort(preset.Port, preset.UseSsl), preset.UseSsl, true);
            }
        }

        return (
            account.CustomImapHost,
            MailTransportSecurityHelper.NormalizeImapPort(account.CustomImapPort, account.CustomImapSsl),
            account.CustomImapSsl,
            false);
    }

    public async Task<ImapClient?> CreateAuthenticatedClientAsync(string accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ConnectAsync(accountId, cancellationToken);
            return result.Client;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ImapClient?> CreateAuthenticatedClientAsync(EmailAccount account, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ConnectAsync(account, cancellationToken);
            return result.Client;
        }
        catch
        {
            return null;
        }
    }
}
