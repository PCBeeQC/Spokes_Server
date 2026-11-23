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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
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
    private readonly ProjectRepository? _projects;
    private readonly ProjectGroupRepository? _projectGroups;
    private readonly QuoteRepository? _quotes;
    private readonly InvoiceRepository? _invoices;
    private readonly ProjectDocumentRepository? _projectDocuments;
    private readonly ProjectReportRepository? _projectReports;
    private readonly PurchaseOrderRepository? _purchaseOrders;
    private readonly IConfiguration? _config;
    private readonly IUserPrivacyService? _userPrivacyService;

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
        TeamRepository? teams = null,
        ProjectRepository? projects = null,
        ProjectGroupRepository? projectGroups = null,
        QuoteRepository? quotes = null,
        InvoiceRepository? invoices = null,
        ProjectDocumentRepository? projectDocuments = null,
        ProjectReportRepository? projectReports = null,
        PurchaseOrderRepository? purchaseOrders = null,
        IConfiguration? config = null,
        IUserPrivacyService? userPrivacyService = null)
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
        _projects = projects;
        _projectGroups = projectGroups;
        _quotes = quotes;
        _invoices = invoices;
        _projectDocuments = projectDocuments;
        _projectReports = projectReports;
        _purchaseOrders = purchaseOrders;
        _config = config;
        _userPrivacyService = userPrivacyService;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts, null, employees);
    }

    private string ResolveDataPath()
    {
        var configured = _config?["DataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var bodyRoot = _cacheMaintenance.EmailBodyPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(bodyRoot), "EmailAccounts", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(bodyRoot));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return Path.GetFullPath("Data");
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
        if (string.Equals(_companyProfile.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Outbound email is unavailable in Family edition.");
        }

        return SendBillInboxEmailInternalAsync(request, createdByEmployeeId: null, isAutomatedSystemSend: true, cancellationToken);
    }

    public async Task SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.Equals(_companyProfile.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Outbound email is unavailable in Family edition.");
        }

        bool isBillInboxAccount = string.Equals(
            request.AccountId,
            CompanyBillInboxConstants.SystemEmployeeId,
            StringComparison.OrdinalIgnoreCase);

        if (request.FileAttachmentPaths is { Count: > 0 })
        {
            throw new UnauthorizedAccessException("Direct filesystem attachment paths are restricted to automated system email workflows.");
        }

        if (string.Equals(request.EmployeeId, CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("System mailbox identifier cannot be used as an interactive sender EmployeeId.");
        }

        Employee? senderEmployee = ValidateInteractiveSenderEmployee(request.EmployeeId, requireResolvedEmployee: isBillInboxAccount || _employees != null);

        if (isBillInboxAccount)
        {
            if (senderEmployee == null || !CanEmployeeSendFromBillInbox(senderEmployee))
            {
                throw new UnauthorizedAccessException("You are not authorized to send email from the Company Bill Inbox.");
            }

            await SendBillInboxEmailInternalAsync(request, createdByEmployeeId: senderEmployee.Id, isAutomatedSystemSend: false, cancellationToken);
            return;
        }

        if (senderEmployee != null && !senderEmployee.HasPermission(AppPermissions.Email.Use))
        {
            throw new UnauthorizedAccessException("Email.Use permission is required to send email.");
        }

        var identity = ResolveIdentity(request);
        if (identity == null)
        {
            throw new DomainException("No Sending Identity is configured or selected. Please create a Sending Identity in Email Settings.", "IDENTITY_NOT_CONFIGURED");
        }

        await SendWithIdentityAsync(
            request,
            identity,
            createdByEmployeeId: request.EmployeeId,
            isBillInboxSend: false,
            isAutomatedSystemSend: false,
            cancellationToken);
    }

    private static bool CanEmployeeSendFromBillInbox(Employee employee)
    {
        return employee.IsAdmin ||
               employee.HasPermission(AppPermissions.Finance.AccessAccounting) ||
               employee.HasPermission(AppPermissions.Purchases.ManageBills);
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

    private async Task SendBillInboxEmailInternalAsync(
        SendEmailRequest request,
        string? createdByEmployeeId,
        bool isAutomatedSystemSend,
        CancellationToken cancellationToken)
    {
        var billSettings = _companyProfile.GetAccounting().BillInbox ?? new CompanyBillInboxSettings();
        var identity = ResolveBillInboxIdentity(request, billSettings);
        if (identity == null)
        {
            throw new DomainException(
                "Company Bill Inbox Sending Identity is not configured.",
                "BILL_INBOX_SMTP_NOT_CONFIGURED");
        }

        await SendWithIdentityAsync(
            request,
            identity,
            createdByEmployeeId,
            isBillInboxSend: true,
            isAutomatedSystemSend,
            cancellationToken);
    }

    private EmailSendingIdentity? ResolveBillInboxIdentity(
        SendEmailRequest? request,
        CompanyBillInboxSettings? billSettings)
    {
        if (_identities == null) return null;

        if (request != null && !string.IsNullOrWhiteSpace(request.SendingIdentityId))
        {
            var requested = _identities.GetById(request.SendingIdentityId.Trim());
            if (billSettings == null ||
                requested is not { IsServerManaged: true } ||
                !IsBoundBillInboxIdentity(requested, billSettings))
            {
                throw new UnauthorizedAccessException("Only the configured server-managed Company Bill Inbox identity can be used.");
            }
            return requested;
        }

        if (billSettings == null) return null;

        return EnumerateBoundBillInboxIdentities(billSettings, stopAtExplicitSendingIdentity: false).FirstOrDefault();
    }

    private bool IsBoundBillInboxIdentity(EmailSendingIdentity candidate, CompanyBillInboxSettings billSettings)
    {
        if (!candidate.IsServerManaged)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(billSettings.SendingIdentityId))
        {
            return string.Equals(candidate.Id, billSettings.SendingIdentityId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return EnumerateBoundBillInboxIdentities(billSettings, stopAtExplicitSendingIdentity: true)
            .Any(i => string.Equals(i.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<EmailSendingIdentity> EnumerateBoundBillInboxIdentities(
        CompanyBillInboxSettings billSettings,
        bool stopAtExplicitSendingIdentity)
    {
        if (_identities == null) yield break;

        if (!string.IsNullOrWhiteSpace(billSettings.SendingIdentityId))
        {
            var configured = _identities.GetById(billSettings.SendingIdentityId.Trim());
            if (configured is { IsServerManaged: true }) yield return configured;
            if (stopAtExplicitSendingIdentity) yield break;
        }

        if (!string.IsNullOrWhiteSpace(billSettings.InboxId) && _inboxes != null)
        {
            var inbox = _inboxes.GetById(billSettings.InboxId.Trim());
            if (inbox is { IsServerManaged: true })
            {
                if (!string.IsNullOrWhiteSpace(inbox.DefaultIdentityId))
                {
                    var inboxDefault = _identities.GetById(inbox.DefaultIdentityId);
                    if (inboxDefault is { IsServerManaged: true }) yield return inboxDefault;
                }

                if (inbox.LinkedIdentityIds != null)
                {
                    foreach (var linkedId in inbox.LinkedIdentityIds)
                    {
                        var linked = _identities.GetById(linkedId);
                        if (linked is { IsServerManaged: true }) yield return linked;
                    }
                }

                foreach (var sourced in _identities.GetAll().Where(i =>
                    i.IsServerManaged &&
                    i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                    string.Equals(i.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return sourced;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(billSettings.AccountId) && _accounts != null)
        {
            var account = _accounts.GetById(billSettings.AccountId.Trim());
            if (account is { IsServerManaged: true })
            {
                if (!string.IsNullOrWhiteSpace(account.DefaultIdentityId))
                {
                    var accDefault = _identities.GetById(account.DefaultIdentityId);
                    if (accDefault is { IsServerManaged: true }) yield return accDefault;
                }

                if (!string.IsNullOrWhiteSpace(account.AutoCreatedIdentityId))
                {
                    var autoCreated = _identities.GetById(account.AutoCreatedIdentityId);
                    if (autoCreated is { IsServerManaged: true }) yield return autoCreated;
                }

                if (account.LinkedIdentityIds != null)
                {
                    foreach (var linkedId in account.LinkedIdentityIds)
                    {
                        var linked = _identities.GetById(linkedId);
                        if (linked is { IsServerManaged: true }) yield return linked;
                    }
                }

                foreach (var bySourceAccount in _identities.GetAll().Where(i =>
                    i.IsServerManaged &&
                    string.Equals(i.SourceAccountId, account.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return bySourceAccount;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(billSettings.EmailAddress))
        {
            foreach (var byEmail in _identities.GetAll().Where(i =>
                i.IsServerManaged &&
                string.Equals(i.EmailAddress, billSettings.EmailAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                yield return byEmail;
            }
        }
    }

    private async Task SendWithIdentityAsync(
        SendEmailRequest request,
        EmailSendingIdentity identity,
        string? createdByEmployeeId,
        bool isBillInboxSend,
        bool isAutomatedSystemSend,
        CancellationToken cancellationToken)
    {
        var (smtpHost, smtpPort, smtpSsl, smtpUsername, smtpPassword, isTrustedServer) = ResolveSmtpConnection(identity);
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            throw new DomainException(
                isBillInboxSend
                    ? "Company Bill Inbox SMTP server is not configured."
                    : "SMTP Server is not configured for the selected Sending Identity.",
                isBillInboxSend ? "BILL_INBOX_SMTP_NOT_CONFIGURED" : "SMTP_NOT_CONFIGURED");
        }

        var billSettings = _companyProfile.GetAccounting().BillInbox;
        string? targetAccountId;
        if (isBillInboxSend)
        {
            targetAccountId = !string.IsNullOrWhiteSpace(billSettings?.AccountId)
                ? billSettings.AccountId
                : (ResolveTargetAccountId(request, identity) ?? CompanyBillInboxConstants.SystemEmployeeId);
        }
        else
        {
            targetAccountId = ResolveTargetAccountId(request, identity);
        }

        var draftStorageAccountId = !string.IsNullOrWhiteSpace(request.AccountId)
            ? request.AccountId
            : targetAccountId;

        var senderAddress = identity.EmailAddress;
        var senderName = !string.IsNullOrWhiteSpace(identity.DisplayName)
            ? identity.DisplayName
            : (isBillInboxSend && !string.IsNullOrWhiteSpace(billSettings?.DisplayName)
                ? billSettings.DisplayName.Trim()
                : senderAddress);

        var senderEmployee = !string.IsNullOrWhiteSpace(createdByEmployeeId)
            ? _employees?.GetById(createdByEmployeeId)
            : null;

        var message = await BuildMimeMessageAsync(
            request,
            senderName,
            senderAddress,
            draftStorageAccountId,
            senderEmployee,
            isAutomatedSystemSend,
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
            bool isSyntheticBillSentinel = string.Equals(
                targetAccountId,
                CompanyBillInboxConstants.SystemEmployeeId,
                StringComparison.OrdinalIgnoreCase);

            await RecordSentMessageAsync(
                targetAccountId,
                senderAddress,
                senderName,
                identity.Id,
                smtpHost,
                message,
                request,
                createdByEmployeeId: createdByEmployeeId,
                skipImapAppend: isSyntheticBillSentinel,
                cancellationToken);
        }
    }

    internal async Task<MimeMessage> BuildMimeMessageAsync(
        SendEmailRequest request,
        string senderName,
        string senderAddress,
        string? draftStorageAccountId,
        Employee? senderEmployee = null,
        bool isAutomatedSystemSend = false,
        CancellationToken cancellationToken = default)
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

        if (request.FileAttachmentPaths is { Count: > 0 })
        {
            await AttachAutomatedSystemFilesAsync(builder, request.FileAttachmentPaths, isAutomatedSystemSend, cancellationToken);
        }

        if (request.DocumentAttachments is { Count: > 0 })
        {
            await AttachAuthorizedDocumentsAsync(builder, request.DocumentAttachments, senderEmployee, isAutomatedSystemSend, cancellationToken);
        }

        if (request.ForwardedEmailAttachments is { Count: > 0 })
        {
            await AttachForwardedEmailAttachmentsAsync(builder, request.ForwardedEmailAttachments, senderEmployee, cancellationToken);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private async Task AttachAutomatedSystemFilesAsync(
        BodyBuilder builder,
        IEnumerable<(string FilePath, string FileName)> fileAttachmentPaths,
        bool isAutomatedSystemSend,
        CancellationToken cancellationToken)
    {
        if (!isAutomatedSystemSend)
        {
            throw new UnauthorizedAccessException("Direct filesystem attachment paths are restricted to automated system sends.");
        }

        var dataRoot = ResolveDataPath();
        foreach (var file in fileAttachmentPaths)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                throw new UnauthorizedAccessException("Attachment path cannot be empty.");
            }

            var fullPath = Path.GetFullPath(file.FilePath);
            if (!IsPathContainedUnder(fullPath, dataRoot))
            {
                throw new UnauthorizedAccessException("Automated attachment path is outside the allowed data directory.");
            }

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var fileBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            var safeFileName = SanitizeAttachmentFileName(file.FileName, Path.GetFileName(fullPath));
            var contentType = safeFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : "application/octet-stream";
            builder.Attachments.Add(safeFileName, fileBytes, ContentType.Parse(contentType));
        }
    }

    private async Task AttachAuthorizedDocumentsAsync(
        BodyBuilder builder,
        IEnumerable<OutboundDocumentAttachmentRef> documentAttachments,
        Employee? senderEmployee,
        bool isAutomatedSystemSend,
        CancellationToken cancellationToken)
    {
        if (!isAutomatedSystemSend && senderEmployee is not { IsActiveUnsuspended: true })
        {
            throw new UnauthorizedAccessException("An active non-suspended employee is required to attach server documents.");
        }

        var pdfRoot = Path.Combine(ResolveDataPath(), "PDFs");
        foreach (var docRef in documentAttachments)
        {
            if (HasUnsafePathToken(docRef.DocumentId))
            {
                throw new UnauthorizedAccessException("Invalid document attachment reference.");
            }

            var (pdfPath, defaultFileName) = ResolveAuthorizedDocumentPdf(docRef, senderEmployee);
            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                throw new UnauthorizedAccessException("Referenced document has no generated PDF path.");
            }

            var fullPdfPath = Path.GetFullPath(pdfPath);
            if (!IsPathContainedUnder(fullPdfPath, pdfRoot) ||
                !fullPdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Referenced document PDF path is outside the allowed PDFs directory.");
            }

            if (!File.Exists(fullPdfPath))
            {
                throw new FileNotFoundException($"Generated PDF for document '{docRef.DocumentId}' was not found on disk.", fullPdfPath);
            }

            var fileBytes = await File.ReadAllBytesAsync(fullPdfPath, cancellationToken);
            var safeFileName = SanitizeAttachmentFileName(docRef.DisplayFileName, defaultFileName);
            builder.Attachments.Add(safeFileName, fileBytes, new ContentType("application", "pdf"));
        }
    }

    private (string PdfPath, string DefaultFileName) ResolveAuthorizedDocumentPdf(
        OutboundDocumentAttachmentRef docRef,
        Employee? senderEmployee)
    {
        switch (docRef.Kind)
        {
            case OutboundDocumentKind.Quote:
            {
                var quote = _quotes?.GetById(docRef.DocumentId);
                var project = quote != null ? _projects?.GetById(quote.ProjectId) : null;
                if (quote == null || project == null ||
                    (senderEmployee != null && !ProjectAccessHelper.CanAccess(senderEmployee, project, _teams, allowAccountingFinancialRead: false)))
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected quote.");
                }
                return (quote.PdfPath, $"Quote_{quote.QuoteNumber}.pdf");
            }
            case OutboundDocumentKind.Invoice:
            {
                var invoice = _invoices?.GetById(docRef.DocumentId);
                if (invoice == null || (senderEmployee != null && !CanEmployeeAccessInvoiceDocument(senderEmployee, invoice)))
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected invoice.");
                }
                return (invoice.PdfPath, $"Invoice_{invoice.InvoiceNumber}.pdf");
            }
            case OutboundDocumentKind.ProjectDocument:
            {
                var doc = _projectDocuments?.GetById(docRef.DocumentId);
                var project = doc != null && !string.IsNullOrEmpty(doc.ProjectId) ? _projects?.GetById(doc.ProjectId) : null;
                if (doc == null || project == null ||
                    (senderEmployee != null && !ProjectAccessHelper.CanAccess(senderEmployee, project, _teams, allowAccountingFinancialRead: false)))
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected project document.");
                }
                return (doc.PdfPath, $"{doc.Name}.pdf");
            }
            case OutboundDocumentKind.BusinessDocument:
            {
                var doc = _projectDocuments?.GetById(docRef.DocumentId);
                if (doc == null || !string.IsNullOrEmpty(doc.ProjectId) ||
                    (senderEmployee != null && !senderEmployee.IsAdmin && !senderEmployee.HasPermission(AppPermissions.BusinessDocuments.View)))
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected business document.");
                }
                return (doc.PdfPath, $"{doc.Name}.pdf");
            }
            case OutboundDocumentKind.ProjectReport:
            {
                var report = _projectReports?.GetById(docRef.DocumentId);
                var project = report != null ? _projects?.GetById(report.ProjectId) : null;
                if (report == null || project == null)
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected project report.");
                }

                if (senderEmployee != null)
                {
                    if (!ProjectAccessHelper.CanAccess(senderEmployee, project, _teams, allowAccountingFinancialRead: false))
                    {
                        throw new UnauthorizedAccessException("You are not authorized to attach the selected project report.");
                    }

                    var privacy = _userPrivacyService ??
                                  (_employees != null && _teams != null
                                      ? new UserPrivacyService(_companyProfile, _employees, _teams)
                                      : null);
                    var allEmployees = _employees?.GetAll() ?? [];
                    if (privacy == null || !ProjectReportService.CanViewerAccessUnredactedReportStatic(report, senderEmployee, privacy, allEmployees))
                    {
                        throw new UnauthorizedAccessException("You are not authorized to send an unredacted PDF of this project report.");
                    }
                }
                return (report.PdfPath, $"{report.ReportNumber}.pdf");
            }
            case OutboundDocumentKind.PurchaseOrder:
            {
                var po = _purchaseOrders?.GetById(docRef.DocumentId);
                if (po == null || (senderEmployee != null && !CanEmployeeAccessPurchaseOrderDocument(senderEmployee, po)))
                {
                    throw new UnauthorizedAccessException("You are not authorized to attach the selected purchase order.");
                }
                return (po.PdfPath, $"PO_{po.PoNumber}.pdf");
            }
            default:
                throw new UnauthorizedAccessException("Unsupported document attachment type.");
        }
    }

    private bool CanEmployeeAccessInvoiceDocument(Employee senderEmployee, Invoice invoice)
    {
        if (senderEmployee.IsAdmin ||
            senderEmployee.HasPermission(AppPermissions.Finance.AccessAccounting) ||
            senderEmployee.HasPermission(AppPermissions.Finance.ApproveInvoices))
        {
            return true;
        }

        if (_projects == null) return false;

        if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
        {
            var group = _projectGroups?.GetById(invoice.ProjectGroupId);
            return group != null &&
                   group.ProjectIds.Count > 0 &&
                   group.ProjectIds.All(pid => _projects.GetById(pid) is { } p &&
                                               ProjectAccessHelper.CanAccess(senderEmployee, p, _teams, allowAccountingFinancialRead: true));
        }

        if (!string.IsNullOrEmpty(invoice.ProjectId))
        {
            return _projects.GetById(invoice.ProjectId) is { } proj &&
                   ProjectAccessHelper.CanAccess(senderEmployee, proj, _teams, allowAccountingFinancialRead: true);
        }

        return false;
    }

    private bool CanEmployeeAccessPurchaseOrderDocument(Employee senderEmployee, PurchaseOrder po)
    {
        if (!senderEmployee.IsAdmin &&
            !senderEmployee.HasPermission(AppPermissions.Purchases.ViewPOs) &&
            !senderEmployee.HasPermission(AppPermissions.Purchases.ManagePOs))
        {
            return false;
        }

        if (senderEmployee.IsAdmin) return true;

        var referencedProjectIds = po.LineItems
            .Select(li => li.ProjectId)
            .Where(pid => !string.IsNullOrWhiteSpace(pid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (referencedProjectIds.Count == 0) return true;
        if (_projects == null) return false;

        bool canReadAccounting = senderEmployee.HasPermission(AppPermissions.Finance.AccessAccounting);
        return referencedProjectIds.All(pid =>
            _projects.GetById(pid) is { } proj &&
            ProjectAccessHelper.CanAccess(senderEmployee, proj, _teams, allowAccountingFinancialRead: canReadAccounting));
    }

    private async Task AttachForwardedEmailAttachmentsAsync(
        BodyBuilder builder,
        IEnumerable<ForwardedEmailAttachmentRef> forwardedAttachments,
        Employee? senderEmployee,
        CancellationToken cancellationToken)
    {
        if (senderEmployee is not { IsActiveUnsuspended: true } || _inboxService == null || _emailMessages == null)
        {
            throw new UnauthorizedAccessException("Authorized employee context is required to forward stored email attachments.");
        }

        var emailRoot = _cacheMaintenance.EmailBodyPath;
        foreach (var fwd in forwardedAttachments)
        {
            if (HasUnsafePathToken(fwd.SourceAccountId) ||
                HasUnsafePathToken(fwd.SourceMessageId) ||
                HasUnsafePathToken(fwd.AttachmentId))
            {
                throw new UnauthorizedAccessException("Invalid forwarded attachment identifier.");
            }

            if (!_inboxService.CanUserAccessEmailMessage(senderEmployee, fwd.SourceAccountId, fwd.SourceMessageId))
            {
                throw new UnauthorizedAccessException("You are not authorized to forward attachments from the referenced message.");
            }

            var sourceMsg = _emailMessages.GetById(fwd.SourceAccountId, fwd.SourceMessageId);
            var attMeta = sourceMsg?.Attachments.FirstOrDefault(a =>
                string.Equals(a.Id, fwd.AttachmentId, StringComparison.Ordinal));
            if (attMeta == null)
            {
                throw new UnauthorizedAccessException("Referenced email attachment metadata was not found.");
            }

            var attPath = Path.GetFullPath(_cacheMaintenance.GetAttachmentPath(fwd.SourceAccountId, fwd.SourceMessageId, attMeta.Id));
            if (!IsPathContainedUnder(attPath, emailRoot))
            {
                throw new UnauthorizedAccessException("Forwarded attachment path is outside the email cache root.");
            }

            if (!File.Exists(attPath))
            {
                continue;
            }

            var fileBytes = await File.ReadAllBytesAsync(attPath, cancellationToken);
            var contentType = ContentType.TryParse(attMeta.ContentType, out var parsed)
                ? parsed
                : new ContentType("application", "octet-stream");
            var safeFileName = SanitizeAttachmentFileName(fwd.DisplayFileName, attMeta.FileName);
            builder.Attachments.Add(safeFileName, fileBytes, contentType);
        }
    }

    private static bool HasUnsafePathToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ||
        token.Contains("..", StringComparison.Ordinal) ||
        token.Contains('/') ||
        token.Contains('\\');

    private static bool IsPathContainedUnder(string fullPath, string directoryPath)
    {
        var normalizedDir = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(fullPath).StartsWith(normalizedDir, comparison);
    }

    private static string SanitizeAttachmentFileName(string? preferredName, string? fallbackName)
    {
        var candidate = !string.IsNullOrWhiteSpace(preferredName) ? preferredName : fallbackName;
        var fileName = Path.GetFileName(candidate?.Trim() ?? string.Empty);
        return string.IsNullOrWhiteSpace(fileName) ? "attachment.bin" : fileName;
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

        if (string.IsNullOrWhiteSpace(employeeId) && string.IsNullOrWhiteSpace(draft.CreatedByEmployeeId))
            return;

        if (!EmailInboxService.CanEmployeeAccessLocalDraft(draft, employeeId, _accounts?.GetById(draft.AccountId), draft.AccountId))
        {
            throw new UnauthorizedAccessException("Cannot attach files from another user's draft.");
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

        return TeamRepository.GetUserTeamIds(_teams, employeeId, _employees?.GetById(employeeId));
    }

    private bool IsIdentityPermittedForUser(EmailSendingIdentity identity, string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return false;

        var billSettings = _companyProfile.GetAccounting().BillInbox;
        var billIdentity = ResolveBillInboxIdentity(null, billSettings);
        if (billIdentity != null &&
            string.Equals(billIdentity.Id, identity.Id, StringComparison.OrdinalIgnoreCase) &&
            _employees?.GetById(employeeId) is { IsActive: true, IsBanned: false, IsSuspended: false } billEmp &&
            CanEmployeeSendFromBillInbox(billEmp))
        {
            return true;
        }

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
        }

        if (usedConfiguredServer && serverConfig!.UseSharedCredentials)
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

    internal string? ResolveTargetAccountId(SendEmailRequest request, EmailSendingIdentity identity)
    {
        bool isPrivateUnsharedPersonalOutgoing =
            identity.SourceType == EmailIdentitySourceType.OutgoingServer &&
            !identity.IsServerManaged &&
            (identity.AllowedUserIds == null || identity.AllowedUserIds.Count == 0) &&
            (identity.AllowedTeamIds == null || identity.AllowedTeamIds.Count == 0);

        if (!isPrivateUnsharedPersonalOutgoing)
        {
            if (!string.IsNullOrWhiteSpace(identity.SourceAccountId))
                return identity.SourceAccountId;

            if (!string.IsNullOrWhiteSpace(identity.SourceInboxId) && _inboxes != null)
            {
                var sourceInbox = _inboxes.GetById(identity.SourceInboxId);
                if (sourceInbox != null && !string.IsNullOrWhiteSpace(sourceInbox.AccountId))
                    return sourceInbox.AccountId;
            }

            var candidateAccountIds = new List<string>();

            if (_inboxes != null)
            {
                // Only explicitly linked inboxes whose container may legitimately host this identity determine Sent storage.
                foreach (var inbox in _inboxes.GetAll())
                {
                    bool isLinked = string.Equals(inbox.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                                    (inbox.LinkedIdentityIds != null && inbox.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase));
                    if (!isLinked || string.IsNullOrWhiteSpace(inbox.AccountId))
                        continue;

                    var backingAccount = _accounts?.GetById(inbox.AccountId);
                    if (backingAccount is { IsArchived: true })
                        continue;

                    bool mailboxIsServerManaged = inbox.IsServerManaged || (backingAccount?.IsServerManaged ?? false);
                    var mailboxOwnerId = backingAccount != null ? backingAccount.OwnerEmployeeId : inbox.OwnerEmployeeId;
                    if (!EmailInboxService.CanMailboxLinkIdentity(identity, mailboxIsServerManaged, mailboxOwnerId))
                        continue;

                    if (!candidateAccountIds.Contains(inbox.AccountId, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateAccountIds.Add(inbox.AccountId);
                    }
                }
            }

            if (_accounts != null)
            {
                foreach (var account in _accounts.GetAll())
                {
                    if (account.IsArchived)
                        continue;

                    bool isLinked = string.Equals(account.AutoCreatedIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(account.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase) ||
                                    (account.LinkedIdentityIds != null && account.LinkedIdentityIds.Contains(identity.Id, StringComparer.OrdinalIgnoreCase));
                    if (!isLinked)
                        continue;

                    if (!EmailInboxService.CanMailboxLinkIdentity(identity, account.IsServerManaged, account.OwnerEmployeeId))
                        continue;

                    if (!candidateAccountIds.Contains(account.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateAccountIds.Add(account.Id);
                    }
                }
            }

            if (candidateAccountIds.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(request.AccountId) &&
                    candidateAccountIds.Contains(request.AccountId, StringComparer.OrdinalIgnoreCase))
                {
                    return request.AccountId;
                }

                return candidateAccountIds[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AccountId))
        {
            if (_accounts == null && !isPrivateUnsharedPersonalOutgoing)
            {
                return request.AccountId;
            }

            var expectedOwnerId = isPrivateUnsharedPersonalOutgoing ? identity.OwnerEmployeeId : request.EmployeeId;
            if (!identity.IsServerManaged &&
                _accounts?.GetById(request.AccountId) is { IsServerManaged: false, IsArchived: false } reqAcc &&
                IsUnsharedPersonalMailbox(reqAcc) &&
                !string.IsNullOrWhiteSpace(expectedOwnerId) &&
                string.Equals(reqAcc.OwnerEmployeeId, expectedOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                return reqAcc.Id;
            }
        }

        var fallbackOwnerId = isPrivateUnsharedPersonalOutgoing ? identity.OwnerEmployeeId : request.EmployeeId;
        if (_accounts != null && !identity.IsServerManaged && !string.IsNullOrWhiteSpace(fallbackOwnerId))
        {
            var owned = _accounts.GetByOwner(fallbackOwnerId)
                .FirstOrDefault(a => !a.IsArchived && !a.IsServerManaged && IsUnsharedPersonalMailbox(a));
            if (owned != null) return owned.Id;
        }

        return null;
    }

    private static bool IsUnsharedPersonalMailbox(EmailAccount account) =>
        (account.AllowedUserIds == null || account.AllowedUserIds.Count == 0) &&
        (account.AllowedTeamIds == null || account.AllowedTeamIds.Count == 0);

    private bool IsBillInboxTargetAccount(string accountId)
    {
        if (string.Equals(accountId, CompanyBillInboxConstants.SystemEmployeeId, StringComparison.OrdinalIgnoreCase))
            return true;

        var billAccountId = _companyProfile.GetAccounting().BillInbox?.AccountId;
        return !string.IsNullOrWhiteSpace(billAccountId) &&
               string.Equals(accountId, billAccountId, StringComparison.OrdinalIgnoreCase);
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
                    BillingStatus = IsBillInboxTargetAccount(accountId)
                        ? CompanyBillInboxConstants.BillingStatus.Approved
                        : null
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
