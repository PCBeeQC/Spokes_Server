using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Configuration;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailDeliveryService : IEmailDeliveryService
{
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EncryptionService _encryptionService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailMessageRepository? _emailMessages;
    private readonly ILogger<EmailDeliveryService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;

    public EmailDeliveryService(
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EncryptionService encryptionService,
        IEmailCacheMaintenanceService cacheMaintenance,
        EmailMessageRepository? emailMessages = null,
        ILogger<EmailDeliveryService>? logger = null,
        IImapConnectionFactory? imapFactory = null)
    {
        _employees = employees;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _encryptionService = encryptionService;
        _cacheMaintenance = cacheMaintenance;
        _emailMessages = emailMessages;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, employees, encryptionService);
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

    public async Task SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        var employeeId = request.EmployeeId;
        var to = request.To;
        var subject = request.Subject;
        var body = request.Body;
        var attachments = request.Attachments;
        var cc = request.Cc;
        var bcc = request.Bcc;
        var existingAttachments = request.ExistingAttachments;
        var draftId = request.DraftId;
        var fileAttachmentPaths = request.FileAttachmentPaths;

        string username;
        string senderName;
        string password;
        string smtpHost;
        int smtpPort;
        bool smtpSsl;

        if (employeeId == CompanyBillInboxConstants.SystemEmployeeId)
        {
            var profile = _companyProfile.Get();
            var billInboxSettings = profile?.Accounting?.BillInbox ?? new CompanyBillInboxSettings();
            var emailSettings = profile?.EmailSettings ?? new EmailServerSettings();

            smtpHost = !string.IsNullOrWhiteSpace(billInboxSettings.SmtpHost) ? billInboxSettings.SmtpHost : (emailSettings.SmtpHost ?? "");
            smtpPort = billInboxSettings.SmtpPort > 0 ? billInboxSettings.SmtpPort : (emailSettings.SmtpPort > 0 ? emailSettings.SmtpPort : 587);
            smtpSsl = billInboxSettings.SmtpSsl;
            username = !string.IsNullOrWhiteSpace(billInboxSettings.SmtpUsername) ? billInboxSettings.SmtpUsername : billInboxSettings.EmailAddress;
            senderName = !string.IsNullOrWhiteSpace(billInboxSettings.DisplayName) ? billInboxSettings.DisplayName : "Facturation";

            if (!string.IsNullOrEmpty(billInboxSettings.EncryptedSmtpPassword))
            {
                password = _encryptionService.Decrypt(billInboxSettings.EncryptedSmtpPassword);
            }
            else if (!string.IsNullOrEmpty(billInboxSettings.EncryptedImapPassword))
            {
                password = _encryptionService.Decrypt(billInboxSettings.EncryptedImapPassword);
            }
            else
            {
                password = "";
            }

            if (string.IsNullOrEmpty(smtpHost))
                throw new DomainException("SMTP Server not configured for company bill inbox.", "SMTP_NOT_CONFIGURED");
        }
        else
        {
            var employee = _employees.GetById(employeeId);
            if (employee == null) throw new DomainException("Employee not found", "EMPLOYEE_NOT_FOUND");

            username = employee.Email;
            senderName = employee.FullName;
            try
            {
                password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("Failed to decrypt email password. Please re-enter your email credentials in settings.", ex);
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw new DomainException("Missing email credentials. Please configure them in your profile.", "EMAIL_CREDENTIALS_MISSING");

            var profile = _companyProfile.Get();
            if (profile == null || string.IsNullOrEmpty(profile.EmailSettings.SmtpHost))
                throw new DomainException("SMTP Server not configured by admin.", "SMTP_NOT_CONFIGURED");

            var settings = profile.EmailSettings;
            smtpHost = settings.SmtpHost;
            smtpPort = settings.SmtpPort;
            smtpSsl = settings.SmtpSsl;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, username));
        message.To.AddRange(InternetAddressList.Parse(to));

        if (!string.IsNullOrWhiteSpace(cc))
            message.Cc.AddRange(InternetAddressList.Parse(cc));

        if (!string.IsNullOrWhiteSpace(bcc))
            message.Bcc.AddRange(InternetAddressList.Parse(bcc));

        message.Subject = subject;

        body = NormalizeQuillHtml(body);

        var builder = new BodyBuilder { HtmlBody = body };

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
            builder.TextBody = System.Net.WebUtility.HtmlDecode(Regex.Replace(body, "<.*?>", " "));
        }
        catch
        {
            builder.TextBody = body;
        }

        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                var bytes = await att.GetBytesAsync(cancellationToken);
                var contentType = !string.IsNullOrWhiteSpace(att.ContentType) ? ContentType.Parse(att.ContentType) : new ContentType("application", "octet-stream");
                builder.Attachments.Add(att.FileName, bytes, contentType);
            }
        }

        if (existingAttachments != null && !string.IsNullOrEmpty(draftId))
        {
            foreach (var att in existingAttachments)
            {
                var filePath = _cacheMaintenance.GetAttachmentPath(employeeId, draftId, att.Id);
                if (File.Exists(filePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    builder.Attachments.Add(att.FileName, fileBytes, ContentType.Parse(att.ContentType));
                }
            }
        }

        if (fileAttachmentPaths != null)
        {
            foreach (var file in fileAttachmentPaths)
            {
                if (File.Exists(file.FilePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                    var contentType = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream";
                    builder.Attachments.Add(file.FileName, fileBytes, ContentType.Parse(contentType));
                }
            }
        }

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();

        await client.ConnectAsync(smtpHost, smtpPort, smtpSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            await client.AuthenticateAsync(username, password, cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        if (employeeId == CompanyBillInboxConstants.SystemEmployeeId && _emailMessages != null)
        {
            try
            {
                var sentEmail = new EmailMessage
                {
                    EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
                    FolderPath = "Sent",
                    Subject = subject,
                    FromAddress = username,
                    FromName = senderName,
                    ToAddresses = [to],
                    Date = DateTimeOffset.UtcNow,
                    Snippet = subject,
                    IsRead = true,
                    BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved
                };

                var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime");
                Directory.CreateDirectory(mimeFolder);
                var mimePath = Path.Combine(mimeFolder, $"{sentEmail.Id}.eml");
                await using var mimeStream = File.Create(mimePath);
                await message.WriteToAsync(mimeStream, cancellationToken);

                _emailMessages.Save(sentEmail);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to record sent email for bill inbox");
            }
        }
        else
        {
            try
            {
                var sentFolderEntity = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.IsSent);
                if (sentFolderEntity != null)
                {
                    using var imapClient = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, cancellationToken);
                    if (imapClient != null)
                    {
                        var imapSentFolder = await imapClient.GetFolderAsync(sentFolderEntity.Path, cancellationToken);
                        if (imapSentFolder != null)
                        {
                            await imapSentFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
                            await imapSentFolder.AppendAsync(message, MessageFlags.Seen, DateTimeOffset.Now, cancellationToken);
                        }
                        await imapClient.DisconnectAsync(true, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to explicitly append to Sent folder");
            }
        }
    }
}
