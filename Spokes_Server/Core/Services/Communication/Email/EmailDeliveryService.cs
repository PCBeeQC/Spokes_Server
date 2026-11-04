using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailDeliveryService : IEmailDeliveryService
{
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EncryptionService _encryptionService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailMessageRepository? _emailMessages;
    private readonly EmailSendingIdentityRepository? _identities;
    private readonly EmailInboxRepository? _inboxes;
    private readonly EmailAccountRepository? _accounts;
    private readonly ILogger<EmailDeliveryService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;
    private readonly EmployeeRepository? _employees;
    private readonly IEmailInboxService? _inboxService;
    private readonly TeamRepository? _teams;

    public EmailDeliveryService(
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EncryptionService encryptionService,
        IEmailCacheMaintenanceService cacheMaintenance,
        EmailMessageRepository? emailMessages = null,
        EmailSendingIdentityRepository? identities = null,
        EmailInboxRepository? inboxes = null,
        EmailAccountRepository? accounts = null,
        ILogger<EmailDeliveryService>? logger = null,
        IImapConnectionFactory? imapFactory = null,
        EmployeeRepository? employees = null,
        IEmailInboxService? inboxService = null,
        TeamRepository? teams = null)
    {
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _encryptionService = encryptionService;
        _cacheMaintenance = cacheMaintenance;
        _emailMessages = emailMessages;
        _identities = identities;
        _inboxes = inboxes;
        _accounts = accounts;
        _logger = logger;
        _employees = employees;
        _inboxService = inboxService;
        _teams = teams;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts, null, employees);
    }

    public static string NormalizeQuillHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        html = Regex.Replace(
            html,
            @"<p(\s[^>]*)style\s*=\s*""([^""]*)""",
            match =>
            {
                var before = match.Groups[1].Value;
                var existingStyle = match.Groups[2].Value;
                return $"<p{before}style=\"margin:0;padding:0;{existingStyle}\"";
            },
            RegexOptions.IgnoreCase);

        html = Regex.Replace(
            html,
            @"<p(\s(?![^>]*style\s*=)|>)",
            match =>
            {
                var afterP = match.Groups[1].Value;
                return afterP == ">"
                    ? "<p style=\"margin:0;padding:0;\">"
                    : "<p style=\"margin:0;padding:0;\" ";
            },
            RegexOptions.IgnoreCase);

        html = Regex.Replace(
            html,
            @"(<p[^>]*>)\s*</p>",
            "$1<br></p>",
            RegexOptions.IgnoreCase);

        return html;
    }

    public Task SendSystemBillInboxEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendViaBillInboxSmtpAsync(request, createdByEmployeeId: null, cancellationToken);
    }

    public async Task SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool isBillInboxAccount = string.Equals(
            request.AccountId,
            CompanyBillInboxConstants.SystemEmployeeId,
            StringComparison.OrdinalIgnoreCase);

        if (isBillInboxAccount && request.IsSystemAutomatedSend)
        {
            await SendViaBillInboxSmtpAsync(request, createdByEmployeeId: null, cancellationToken);
            return;
        }

        if (string.Equals(request.EmployeeId, CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("System mailbox identifier cannot be used as an interactive sender EmployeeId.");
        }

        Employee? senderEmployee = ValidateInteractiveSenderEmployee(request.EmployeeId, requireResolvedEmployee: isBillInboxAccount || _employees != null);

        if (isBillInboxAccount)
        {
            if (senderEmployee == null ||
                !(senderEmployee.IsAdmin ||
                  senderEmployee.HasPermission(AppPermissions.Finance.AccessAccounting) ||
                  senderEmployee.HasPermission(AppPermissions.Purchases.ManageBills)))
            {
                throw new UnauthorizedAccessException("You are not authorized to send email from the Company Bill Inbox.");
            }

            await SendViaBillInboxSmtpAsync(request, createdByEmployeeId: senderEmployee.Id, cancellationToken);
            return;
        }

        var identity = ResolveIdentity(request);
        if (identity == null)
        {
            throw new DomainException("No Sending Identity is configured or selected. Please create a Sending Identity in Email Settings.", "IDENTITY_NOT_CONFIGURED");
        }

        var (smtpHost, smtpPort, smtpSsl, smtpUsername, smtpPassword, isTrustedServer) = ResolveSmtpConnection(identity);
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            throw new DomainException("SMTP Server is not configured for the selected Sending Identity.", "SMTP_NOT_CONFIGURED");
        }

        var targetAccountId = ResolveTargetAccountId(request, identity);
        var draftStorageAccountId = !string.IsNullOrWhiteSpace(request.AccountId) ? request.AccountId : targetAccountId;

        var senderAddress = identity.EmailAddress;
        var senderName = !string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.DisplayName : senderAddress;

        var message = await BuildMimeMessageAsync(
            request,
            senderName,
            senderAddress,
            draftStorageAccountId,
            cancellationToken);

        var effectivePort = MailTransportSecurityHelper.NormalizeSmtpPort(smtpPort, smtpSsl);
        var socketOptions = MailTransportSecurityHelper.ResolveSmtpTlsOptions(
            effectivePort,
            smtpSsl,
            allowPlaintext: isTrustedServer);

        using var client = new SmtpClient();
        await MailTransportSecurityHelper.ConnectMailServiceAsync(
            client,
            smtpHost,
            effectivePort,
            socketOptions,
            requireSafeExternalHost: !isTrustedServer,
            cancellationToken);

        if (!string.IsNullOrEmpty(smtpUsername) && !string.IsNullOrEmpty(smtpPassword))
        {
            await client.AuthenticateAsync(smtpUsername, smtpPassword, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        if (!string.IsNullOrEmpty(targetAccountId))
        {
            await RecordSentMessageAsync(
                targetAccountId,
                senderAddress,
                senderName,
                identity.Id,
                smtpHost,
                message,
                request,
                createdByEmployeeId: request.EmployeeId,
                skipImapAppend: false,
                cancellationToken);
        }
    }

    private Employee? ValidateInteractiveSenderEmployee(string employeeId, bool requireResolvedEmployee)
    {
        if (requireResolvedEmployee)
        {
            if (string.IsNullOrWhiteSpace(employeeId) || _employees == null)
            {
                throw new UnauthorizedAccessException("An authenticated active employee is required to send email.");
            }

            var employee = _employees.GetById(employeeId);
            if (employee == null || !employee.IsActive || employee.IsBanned || employee.IsSuspended)
            {
                throw new UnauthorizedAccessException("Sender employee account is inactive, suspended, or unauthorized.");
            }

            return employee;
        }

        return null;
    }

    private async Task SendViaBillInboxSmtpAsync(
        SendEmailRequest request,
        string? createdByEmployeeId,
        CancellationToken cancellationToken)
    {
        var billSettings = _companyProfile.GetAccounting().BillInbox ?? new CompanyBillInboxSettings();
        var emailSettings = _companyProfile.GetEmailSettings();

        string smtpHost;
        int smtpPort;
        SecureSocketOptions socketOptions;
        string smtpUsername;
        string smtpPassword = string.Empty;

        var senderAddress = billSettings.EmailAddress?.Trim() ?? string.Empty;
        var senderName = !string.IsNullOrWhiteSpace(billSettings.DisplayName)
            ? billSettings.DisplayName.Trim()
            : senderAddress;

        if (!string.IsNullOrWhiteSpace(billSettings.SmtpHost))
        {
            smtpHost = billSettings.SmtpHost.Trim();
            smtpPort = MailTransportSecurityHelper.NormalizeSmtpPort(billSettings.SmtpPort, billSettings.SmtpSsl);
            socketOptions = MailTransportSecurityHelper.ResolveBillInboxSmtpTlsOptions(billSettings);
            smtpUsername = !string.IsNullOrWhiteSpace(billSettings.SmtpUsername)
                ? billSettings.SmtpUsername
                : senderAddress;

            var encrypted = !string.IsNullOrEmpty(billSettings.EncryptedSmtpPassword)
                ? billSettings.EncryptedSmtpPassword
                : billSettings.EncryptedImapPassword;
            if (!string.IsNullOrEmpty(encrypted))
            {
                smtpPassword = _encryptionService.Decrypt(encrypted);
            }
        }
        else if (emailSettings.SendingServers.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Host)) is { } fallbackServer)
        {
            smtpHost = fallbackServer.Host.Trim();
            smtpPort = MailTransportSecurityHelper.NormalizeSmtpPort(fallbackServer.Port, fallbackServer.UseSsl);
            socketOptions = MailTransportSecurityHelper.ResolveSmtpTlsOptions(smtpPort, fallbackServer.UseSsl, allowPlaintext: true);

            if (fallbackServer.UseSharedCredentials)
            {
                smtpUsername = fallbackServer.SharedUsername;
                if (!string.IsNullOrEmpty(fallbackServer.EncryptedSharedPassword))
                {
                    smtpPassword = _encryptionService.Decrypt(fallbackServer.EncryptedSharedPassword);
                }
            }
            else
            {
                smtpUsername = !string.IsNullOrWhiteSpace(billSettings.SmtpUsername)
                    ? billSettings.SmtpUsername
                    : senderAddress;
                var encrypted = !string.IsNullOrEmpty(billSettings.EncryptedSmtpPassword)
                    ? billSettings.EncryptedSmtpPassword
                    : billSettings.EncryptedImapPassword;
                if (!string.IsNullOrEmpty(encrypted))
                {
                    smtpPassword = _encryptionService.Decrypt(encrypted);
                }
            }
        }
        else
        {
            smtpHost = string.Empty;
            smtpPort = 587;
            socketOptions = SecureSocketOptions.StartTls;
            smtpUsername = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(senderAddress) || string.IsNullOrWhiteSpace(smtpHost))
        {
            throw new DomainException("Company Bill Inbox email address or SMTP server is not configured.", "BILL_INBOX_SMTP_NOT_CONFIGURED");
        }

        var message = await BuildMimeMessageAsync(
            request,
            senderName,
            senderAddress,
            CompanyBillInboxConstants.SystemEmployeeId,
            cancellationToken);

        using var client = new SmtpClient();
        await MailTransportSecurityHelper.ConnectMailServiceAsync(
            client,
            smtpHost,
            smtpPort,
            socketOptions,
            requireSafeExternalHost: false,
            cancellationToken);

        if (!string.IsNullOrEmpty(smtpUsername) && !string.IsNullOrEmpty(smtpPassword))
        {
            await client.AuthenticateAsync(smtpUsername, smtpPassword, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        await RecordSentMessageAsync(
            CompanyBillInboxConstants.SystemEmployeeId,
            senderAddress,
            senderName,
            sendingIdentityId: null,
            smtpHost,
            message,
            request,
            createdByEmployeeId: createdByEmployeeId,
            skipImapAppend: true,
            cancellationToken);
    }

    private async Task<MimeMessage> BuildMimeMessageAsync(
        SendEmailRequest request,
        string senderName,
        string senderAddress,
        string? draftStorageAccountId,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderAddress));
        message.To.AddRange(InternetAddressList.Parse(request.To));

        if (!string.IsNullOrWhiteSpace(request.Cc))
            message.Cc.AddRange(InternetAddressList.Parse(request.Cc));

        if (!string.IsNullOrWhiteSpace(request.Bcc))
            message.Bcc.AddRange(InternetAddressList.Parse(request.Bcc));

        if (!string.IsNullOrWhiteSpace(request.InReplyTo))
            message.InReplyTo = request.InReplyTo.Trim('<', '>');

        if (request.References != null)
        {
            foreach (var refId in request.References.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                message.References.Add(refId.Trim('<', '>'));
            }
        }

        message.Subject = request.Subject;

        var normalizedBody = NormalizeQuillHtml(request.Body);
        var builder = new BodyBuilder { HtmlBody = normalizedBody };

        var htmlBody = builder.HtmlBody;
        if (!string.IsNullOrEmpty(htmlBody))
        {
            var matches = Regex.Matches(htmlBody, @"src=[""']data:(image/[^;]+);base64,([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                try
                {
                    var mimeTypeString = match.Groups[1].Value;
                    var base64Data = match.Groups[2].Value;
                    var cid = Guid.NewGuid().ToString("N");

                    var extension = mimeTypeString.Split('/').LastOrDefault() ?? "png";
                    var bytes = Convert.FromBase64String(base64Data);

                    var imageResource = builder.LinkedResources.Add("image_" + cid + "." + extension, bytes, ContentType.Parse(mimeTypeString));
                    imageResource.ContentId = cid;

                    htmlBody = htmlBody.Replace(match.Value, $"src=\"cid:{cid}\"");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to parse inline image");
                }
            }
            builder.HtmlBody = htmlBody;
        }

        try
        {
            builder.TextBody = System.Net.WebUtility.HtmlDecode(Regex.Replace(normalizedBody, "<.*?>", " "));
        }
        catch
        {
            builder.TextBody = normalizedBody;
        }

        if (request.Attachments != null)
        {
            foreach (var att in request.Attachments)
            {
                var bytes = await att.GetBytesAsync(cancellationToken);
                var contentType = !string.IsNullOrWhiteSpace(att.ContentType)
                    ? ContentType.Parse(att.ContentType)
                    : new ContentType("application", "octet-stream");
                builder.Attachments.Add(att.FileName, bytes, contentType);
            }
        }

        if (request.ExistingAttachments is { Count: > 0 } &&
            !string.IsNullOrEmpty(request.DraftId) &&
            !string.IsNullOrEmpty(draftStorageAccountId))
        {
            VerifyDraftAttachmentOwnership(draftStorageAccountId, request.DraftId, request.EmployeeId);

            foreach (var att in request.ExistingAttachments)
            {
                var filePath = _cacheMaintenance.GetAttachmentPath(draftStorageAccountId, request.DraftId, att.Id);
                if (File.Exists(filePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    var contentType = !string.IsNullOrWhiteSpace(att.ContentType)
                        ? ContentType.Parse(att.ContentType)
                        : new ContentType("application", "octet-stream");
                    builder.Attachments.Add(att.FileName, fileBytes, contentType);
                }
            }
        }

        if (request.FileAttachmentPaths != null)
        {
            foreach (var file in request.FileAttachmentPaths)
            {
                if (File.Exists(file.FilePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                    var contentType = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                        ? "application/pdf"
                        : "application/octet-stream";
                    builder.Attachments.Add(file.FileName, fileBytes, ContentType.Parse(contentType));
                }
            }
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private void VerifyDraftAttachmentOwnership(string draftAccountId, string draftId, string employeeId)
    {
        if (_emailMessages == null) return;

        var draft = _emailMessages.GetById(draftAccountId, draftId) ?? _emailMessages.GetById(draftId);
        if (draft == null ||
            !string.Equals(draft.FolderPath, "LocalDrafts", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Referenced draft for existing attachments was not found.");
        }

        if (!string.IsNullOrWhiteSpace(draft.CreatedByEmployeeId))
        {
            if (!string.Equals(draft.CreatedByEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Cannot attach files from another user's draft.");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(employeeId))
        {
            var account = _accounts?.GetById(draft.AccountId);
            bool isLegacyPersonalOwner = account != null
                ? (!account.IsServerManaged && string.Equals(account.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                : string.Equals(draft.AccountId, employeeId, StringComparison.OrdinalIgnoreCase);

            if (!isLegacyPersonalOwner)
            {
                throw new UnauthorizedAccessException("Cannot attach files from another user's draft.");
            }
        }
    }

    private EmailSendingIdentity? ResolveIdentity(SendEmailRequest request)
    {
        if (_identities == null) return null;

        bool hasEmployeeContext = !string.IsNullOrWhiteSpace(request.EmployeeId);

        if (!string.IsNullOrWhiteSpace(request.SendingIdentityId))
        {
            var byId = _identities.GetById(request.SendingIdentityId);
            if (byId != null)
            {
                if (!hasEmployeeContext && _employees == null)
                {
                    return byId;
                }

                if (hasEmployeeContext && IsIdentityPermittedForUser(byId, request.EmployeeId))
                {
                    return byId;
                }

                throw new UnauthorizedAccessException("You are not authorized to send from the selected identity.");
            }

            if (hasEmployeeContext)
            {
                throw new DomainException("The selected Sending Identity no longer exists.", "IDENTITY_NOT_FOUND");
            }
        }

        if (hasEmployeeContext)
        {
            if (_inboxService != null)
            {
                var permittedList = _inboxService.GetPermittedIdentities(request.EmployeeId);
                return _inboxService.SelectDefaultIdentity(permittedList, request.EmployeeId);
            }

            var teamIds = GetUserTeamIds(request.EmployeeId);
            var userIdentities = _identities.GetAccessibleForUser(request.EmployeeId, teamIds);
            if (userIdentities.Count > 0) return userIdentities[0];

            return _identities.GetAll().FirstOrDefault(i => IsIdentityPermittedForUser(i, request.EmployeeId));
        }

        return null;
    }

    private HashSet<string> GetUserTeamIds(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_inboxService != null)
            return _inboxService.GetUserTeamIds(employeeId);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emp = _employees?.GetById(employeeId);
        if (emp != null && !string.IsNullOrWhiteSpace(emp.TeamId))
        {
            result.Add(emp.TeamId);
        }

        if (_teams != null)
        {
            foreach (var team in _teams.GetAll())
            {
                if (string.Equals(team.LeaderId, employeeId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(team.Id);
                }
            }
        }

        return result;
    }

    private bool IsIdentityPermittedForUser(EmailSendingIdentity identity, string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return false;

        if (_inboxService != null)
        {
            return _inboxService.IsIdentityPermittedForUser(employeeId, identity);
        }

        var teamIds = GetUserTeamIds(employeeId);

        if (!string.IsNullOrWhiteSpace(identity.SourceAccountId) && _accounts != null)
        {
            var sourceAcc = _accounts.GetById(identity.SourceAccountId);
            if (sourceAcc is { IsArchived: true })
            {
                return false;
            }
        }

        if (identity.CanUserAccess(employeeId, teamIds)) return true;

        if (_inboxes != null && _inboxes.GetAll().Any(i =>
            (string.Equals(i.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(i.Id, identity.SourceInboxId, StringComparison.OrdinalIgnoreCase) ||
             (i.LinkedIdentityIds != null && i.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase))) &&
            i.CanUserAccess(employeeId, teamIds)))
        {
            return true;
        }

        if (_accounts != null && _accounts.GetAll().Any(a =>
            !a.IsArchived &&
            (string.Equals(a.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a.AutoCreatedIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a.Id, identity.SourceAccountId, StringComparison.OrdinalIgnoreCase) ||
             (a.LinkedIdentityIds != null && a.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase))) &&
            a.CanUserAccess(employeeId, teamIds)))
        {
            return true;
        }

        return false;
    }

    private (string Host, int Port, bool UseSsl, string Username, string Password, bool IsTrustedServer) ResolveSmtpConnection(EmailSendingIdentity identity)
    {
        var settings = _companyProfile.Get()?.EmailSettings;

        EmailAccount? sourceAccount = null;
        if (_accounts != null)
        {
            if (!string.IsNullOrWhiteSpace(identity.SourceAccountId))
            {
                sourceAccount = _accounts.GetById(identity.SourceAccountId);
            }
            sourceAccount ??= _accounts.GetAll().FirstOrDefault(a =>
                string.Equals(a.AutoCreatedIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.EmailAddress, identity.EmailAddress, StringComparison.OrdinalIgnoreCase));
        }

        var outgoingServerId = identity.SendingServerConfigId;
        if (string.IsNullOrWhiteSpace(outgoingServerId) && sourceAccount != null)
        {
            outgoingServerId = sourceAccount.OutgoingServerConfigId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outgoingServerId) && !string.IsNullOrWhiteSpace(sourceAccount.IncomingServerConfigId))
            {
                var incoming = settings?.IncomingServers?.FirstOrDefault(s =>
                    string.Equals(s.Id, sourceAccount.IncomingServerConfigId, StringComparison.OrdinalIgnoreCase));
                outgoingServerId = incoming?.DefaultOutgoingServerId ?? string.Empty;
            }
        }

        var serverConfig = !string.IsNullOrWhiteSpace(outgoingServerId)
            ? settings?.SendingServers?.FirstOrDefault(s => string.Equals(s.Id, outgoingServerId, StringComparison.OrdinalIgnoreCase))
            : null;

        string host;
        int port;
        bool useSsl;
        string username;
        string encryptedPassword;
        bool usedConfiguredServer;

        if (serverConfig != null && !string.IsNullOrWhiteSpace(serverConfig.Host))
        {
            host = serverConfig.Host;
            port = serverConfig.Port > 0 ? serverConfig.Port : 465;
            useSsl = serverConfig.UseSsl;
            usedConfiguredServer = true;

            if (serverConfig.UseSharedCredentials)
            {
                username = serverConfig.SharedUsername;
                encryptedPassword = serverConfig.EncryptedSharedPassword;
            }
            else if (!string.IsNullOrWhiteSpace(identity.EncryptedSmtpPassword) || sourceAccount == null)
            {
                username = !string.IsNullOrWhiteSpace(identity.SmtpUsername) ? identity.SmtpUsername : identity.EmailAddress;
                encryptedPassword = identity.EncryptedSmtpPassword;
            }
            else
            {
                username = !string.IsNullOrWhiteSpace(sourceAccount.Username) ? sourceAccount.Username : sourceAccount.EmailAddress;
                encryptedPassword = sourceAccount.EncryptedPassword;
            }
        }
        else
        {
            usedConfiguredServer = false;
            host = !string.IsNullOrWhiteSpace(identity.CustomSmtpHost)
                ? identity.CustomSmtpHost
                : (sourceAccount?.CustomSmtpHost ?? string.Empty);
            port = identity.CustomSmtpPort > 0
                ? identity.CustomSmtpPort
                : (sourceAccount is { CustomSmtpPort: > 0 } ? sourceAccount.CustomSmtpPort : 587);
            useSsl = !string.IsNullOrWhiteSpace(identity.CustomSmtpHost)
                ? identity.CustomSmtpSsl
                : (sourceAccount?.CustomSmtpSsl ?? true);

            if (!string.IsNullOrWhiteSpace(identity.EncryptedSmtpPassword) || sourceAccount == null)
            {
                username = !string.IsNullOrWhiteSpace(identity.SmtpUsername) ? identity.SmtpUsername : identity.EmailAddress;
                encryptedPassword = identity.EncryptedSmtpPassword;
            }
            else
            {
                username = !string.IsNullOrWhiteSpace(sourceAccount.Username) ? sourceAccount.Username : sourceAccount.EmailAddress;
                encryptedPassword = sourceAccount.EncryptedPassword;
            }
        }

        string password = string.Empty;
        if (!string.IsNullOrEmpty(encryptedPassword))
        {
            try
            {
                password = _encryptionService.Decrypt(encryptedPassword);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("Failed to decrypt SMTP password. Please re-enter the SMTP credentials.", ex);
            }
        }

        bool isTrustedServer = identity.IsServerManaged || (sourceAccount?.IsServerManaged == true) || usedConfiguredServer;
        return (host, port, useSsl, username, password, isTrustedServer);
    }

    private string? ResolveTargetAccountId(SendEmailRequest request, EmailSendingIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(request.AccountId))
        {
            if (_accounts == null || _accounts.GetById(request.AccountId) != null)
            {
                return request.AccountId;
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.SourceAccountId))
            return identity.SourceAccountId;

        if (!string.IsNullOrWhiteSpace(identity.SourceInboxId) && _inboxes != null)
        {
            var sourceInbox = _inboxes.GetById(identity.SourceInboxId);
            if (sourceInbox != null && !string.IsNullOrWhiteSpace(sourceInbox.AccountId))
                return sourceInbox.AccountId;
        }

        if (_inboxes != null)
        {
            var matchingInbox = _inboxes.GetAll().FirstOrDefault(i =>
                string.Equals(i.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                (i.LinkedIdentityIds != null && i.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase)) ||
                (i.TargetAddresses != null && i.TargetAddresses.Any(t => string.Equals(t, identity.EmailAddress, StringComparison.OrdinalIgnoreCase))));
            if (matchingInbox != null && !string.IsNullOrWhiteSpace(matchingInbox.AccountId))
                return matchingInbox.AccountId;
        }

        if (_accounts != null)
        {
            var matchingAccount = _accounts.GetAll().FirstOrDefault(a =>
                !a.IsArchived &&
                (string.Equals(a.AutoCreatedIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                 (a.LinkedIdentityIds != null && a.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase))));
            if (matchingAccount != null)
                return matchingAccount.Id;

            if (!string.IsNullOrWhiteSpace(request.EmployeeId))
            {
                var owned = _accounts.GetByOwner(request.EmployeeId).FirstOrDefault(a => !a.IsArchived);
                if (owned != null) return owned.Id;
            }
        }

        return null;
    }

    private async Task RecordSentMessageAsync(
        string accountId,
        string senderAddress,
        string senderName,
        string? sendingIdentityId,
        string smtpHost,
        MimeMessage message,
        SendEmailRequest request,
        string? createdByEmployeeId,
        bool skipImapAppend,
        CancellationToken cancellationToken)
    {
        var folders = _emailFolders.GetByAccount(accountId);
        var sentFolderEntity = folders.FirstOrDefault(f => f.IsSent);

        if (_emailMessages != null)
        {
            try
            {
                if (sentFolderEntity == null)
                {
                    sentFolderEntity = new EmailFolder
                    {
                        AccountId = accountId,
                        Name = "Sent",
                        Path = "Sent",
                        IsSent = true
                    };
                    _emailFolders.Save(sentFolderEntity);
                }

                var toList = message.To.Mailboxes.Select(m => m.Address).ToList();
                var ccList = message.Cc.Mailboxes.Select(m => m.Address).ToList();
                var bccList = message.Bcc.Mailboxes.Select(m => m.Address).ToList();

                var sentEmail = new EmailMessage
                {
                    AccountId = accountId,
                    FolderPath = sentFolderEntity.Path,
                    UniqueId = _emailMessages.GetNextLocalUid(accountId, sentFolderEntity.Path),
                    GlobalMessageId = message.MessageId ?? string.Empty,
                    Subject = request.Subject,
                    FromAddress = senderAddress,
                    FromName = senderName,
                    SendingIdentityId = sendingIdentityId,
                    CreatedByEmployeeId = createdByEmployeeId,
                    ToAddresses = toList,
                    CcAddresses = ccList,
                    BccAddresses = bccList,
                    ReceivingAddresses = toList.Concat(ccList).Concat([senderAddress]).Select(a => a.ToLowerInvariant()).Distinct().ToList(),
                    InReplyTo = request.InReplyTo,
                    References = request.References ?? [],
                    Date = DateTimeOffset.UtcNow,
                    Snippet = request.Subject,
                    IsRead = true,
                    IsLocalOnly = true,
                    BillingStatus = string.Equals(accountId, CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase)
                        ? CompanyBillInboxConstants.BillingStatus.Approved
                        : CompanyBillInboxConstants.BillingStatus.Unprocessed
                };

                var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Mime");
                Directory.CreateDirectory(mimeFolder);
                var mimePath = Path.Combine(mimeFolder, $"{sentEmail.Id}.eml");
                await using (var mimeStream = File.Create(mimePath))
                {
                    await message.WriteToAsync(mimeStream, cancellationToken);
                }

                _emailMessages.Save(sentEmail);
                sentFolderEntity.TotalCount++;
                _emailFolders.Save(sentFolderEntity);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to record local sent email for account {AccountId}", accountId);
            }
        }

        if (skipImapAppend)
        {
            return;
        }

        // Gmail SMTP automatically appends sent messages to [Gmail]/Sent Mail; skip redundant IMAP APPEND.
        bool isGmailSmtp = !string.IsNullOrWhiteSpace(smtpHost) &&
                           (smtpHost.Contains("smtp.gmail.com", StringComparison.OrdinalIgnoreCase) ||
                            smtpHost.Contains("smtp.googlemail.com", StringComparison.OrdinalIgnoreCase));

        if (!isGmailSmtp && sentFolderEntity != null)
        {
            var sentPath = sentFolderEntity.Path;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var imapClient = await _imapFactory.CreateAuthenticatedClientAsync(accountId, CancellationToken.None);
                    if (imapClient != null)
                    {
                        var imapSentFolder = await imapClient.GetFolderAsync(sentPath, CancellationToken.None);
                        if (imapSentFolder != null)
                        {
                            await imapSentFolder.OpenAsync(FolderAccess.ReadWrite, CancellationToken.None);
                            await imapSentFolder.AppendAsync(message, MessageFlags.Seen, DateTimeOffset.Now, CancellationToken.None);
                        }
                        await imapClient.DisconnectAsync(true, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to append sent email to IMAP Sent folder for account {AccountId} in background", accountId);
                }
            });
        }
    }
}
