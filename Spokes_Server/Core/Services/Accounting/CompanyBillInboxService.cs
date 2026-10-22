namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

public class CompanyBillInboxService
{
    private readonly Database _db;
    private readonly IFileService _fileService;
    private readonly PdfInvoiceInspectionService _pdfInspectionService;
    private readonly SmartPoMatchingEngine _poMatchingEngine;
    private readonly EncryptionService _encryptionService;
    private readonly IEmailCacheMaintenanceService? _cacheMaintenance;
    private readonly IEmailDeliveryService? _emailDeliveryService;
    private readonly ILogger<CompanyBillInboxService>? _logger;
    public static event Action? OnInboxIngested;
    public static void NotifyInboxIngested() => OnInboxIngested?.Invoke();

    public CompanyBillInboxService(
        Database db,
        IFileService fileService,
        PdfInvoiceInspectionService pdfInspectionService,
        SmartPoMatchingEngine poMatchingEngine,
        EncryptionService encryptionService,
        IEmailCacheMaintenanceService? cacheMaintenance = null,
        IEmailDeliveryService? emailDeliveryService = null,
        ILogger<CompanyBillInboxService>? logger = null)
    {
        _db = db;
        _fileService = fileService;
        _pdfInspectionService = pdfInspectionService;
        _poMatchingEngine = poMatchingEngine;
        _encryptionService = encryptionService;
        _cacheMaintenance = cacheMaintenance;
        _emailDeliveryService = emailDeliveryService;
        _logger = logger;
    }

    public CompanyBillInboxSettings GetSettings()
    {
        var settings = _db.CompanyProfile.GetAccounting().BillInbox ?? new CompanyBillInboxSettings();
        if (string.IsNullOrWhiteSpace(settings.DeletedFolderName))
        {
            settings.DeletedFolderName = "SPOKES_DELETED";
        }
        return settings;
    }

    public void SaveSettings(CompanyBillInboxSettings settings)
    {
        var profile = _db.CompanyProfile.Get();
        profile.Accounting.BillInbox = settings;
        _db.CompanyProfile.Save(profile);
    }

    /// <summary>
    /// Tests the IMAP connection with provided credentials.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(CompanyBillInboxSettings settings, string? plainPassword = null, CancellationToken cancellationToken = default)
    {
        var emailSettings = _db.CompanyProfile.GetEmailSettings();
        var host = !string.IsNullOrWhiteSpace(settings.ImapHost) ? settings.ImapHost : emailSettings.ImapHost;
        var port = settings.ImapPort > 0 ? settings.ImapPort : emailSettings.ImapPort;
        var useSsl = settings.ImapSsl;
        var user = !string.IsNullOrWhiteSpace(settings.ImapUsername) ? settings.ImapUsername : settings.EmailAddress;

        var password = !string.IsNullOrEmpty(plainPassword)
            ? plainPassword
            : (!string.IsNullOrEmpty(settings.EncryptedImapPassword) ? _encryptionService.Decrypt(settings.EncryptedImapPassword) : "");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "Host, email/username, and password are required.");
        }

        try
        {
            using var client = new ImapClient();
            var options = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(host, port, options, cancellationToken);
            await client.AuthenticateAsync(user, password, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return (true, "Connection successful!");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Bill inbox test connection failed for {User}@{Host}", user, host);
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<(ImapClient? Client, string? Error)> ConnectImapClientAsync(CompanyBillInboxSettings settings, CancellationToken cancellationToken = default)
    {
        var emailSettings = _db.CompanyProfile.GetEmailSettings();
        var host = !string.IsNullOrWhiteSpace(settings.ImapHost) ? settings.ImapHost : emailSettings.ImapHost;
        var port = settings.ImapPort > 0 ? settings.ImapPort : emailSettings.ImapPort;
        var useSsl = settings.ImapSsl;
        var user = !string.IsNullOrWhiteSpace(settings.ImapUsername) ? settings.ImapUsername : settings.EmailAddress;

        string password;
        try
        {
            password = _encryptionService.Decrypt(settings.EncryptedImapPassword);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to decrypt IMAP password for bill inbox");
            return (null, "Decryption error on stored password");
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "Missing host or credentials");
        }

        try
        {
            var client = new ImapClient();
            client.Timeout = 10000;
            var options = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(host, port, options, cancellationToken);
            await client.AuthenticateAsync(user, password, cancellationToken);
            return (client, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to connect to IMAP server {Host}:{Port}", host, port);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Polls the company billing mailbox, syncs new messages, extracts invoices, and generates draft bills.
    /// </summary>
    public async Task<int> CheckInboxAsync(CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.EmailAddress))
        {
            return 0;
        }

        var (client, error) = await ConnectImapClientAsync(settings, cancellationToken);
        if (client == null)
        {
            if (settings.LastCheckStatus != error)
            {
                settings.LastCheckStatus = error;
                settings.LastCheckedAt = DateTime.UtcNow;
                SaveSettings(settings);
            }
            return 0;
        }

        int processedCount = 0;
        try
        {
            using (client)
            {
                var folderName = string.IsNullOrWhiteSpace(settings.FolderToMonitor) ? "INBOX" : settings.FolderToMonitor;
                var folder = await client.GetFolderAsync(folderName, cancellationToken);
                await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            // Fetch unseen or recent messages
            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.NotSeen, cancellationToken);
            if (uids.Count == 0)
            {
                // If no unread, check last 15 messages to ensure no missed messages
                var count = folder.Count;
                if (count > 0)
                {
                    var start = Math.Max(0, count - 15);
                    var rangeUids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, cancellationToken);
                    uids = rangeUids.Skip(start).ToList();
                }
            }

            foreach (var uid in uids)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Check if message already recorded in Spokes
                var existing = _db.EmailMessages.GetByUniqueId(CompanyBillInboxConstants.SystemEmployeeId, uid.Id);

                if (existing != null) continue;

                var mimeMessage = await folder.GetMessageAsync(uid, cancellationToken);
                var email = await ProcessMimeMessageAsync(mimeMessage, uid.Id, cancellationToken);
                if (email != null)
                {
                    processedCount++;
                }

                // Mark seen
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
            }

            await client.DisconnectAsync(true, cancellationToken);

                settings.LastCheckedAt = DateTime.UtcNow;
                settings.LastCheckStatus = "Success";
                settings.LastIngestedCount = processedCount;
                SaveSettings(settings);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to sync company bill inbox");
            settings.LastCheckedAt = DateTime.UtcNow;
            settings.LastCheckStatus = $"Error: {ex.Message}";
            SaveSettings(settings);
        }

        return processedCount;
    }

    private InvoiceExtractionContext BuildExtractionContext(CompanyBillInboxSettings? settings = null)
    {
        settings ??= GetSettings();
        var profile = _db.CompanyProfile.Get();
        var ctx = new InvoiceExtractionContext
        {
            DefaultCurrency = !string.IsNullOrWhiteSpace(settings.DefaultCurrency) ? settings.DefaultCurrency : "CAD"
        };

        if (!string.IsNullOrWhiteSpace(profile.CompanyName))
        {
            ctx.BuyerCompanyName = profile.CompanyName.Trim();
            if (!ctx.BuyerAliases.Contains(ctx.BuyerCompanyName, StringComparer.OrdinalIgnoreCase))
            {
                ctx.BuyerAliases.Add(ctx.BuyerCompanyName);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.EmailAddress) && settings.EmailAddress.Contains('@'))
        {
            var domain = settings.EmailAddress[(settings.EmailAddress.IndexOf('@') + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(domain) && !IsCommonEmailDomain(domain) &&
                !ctx.BuyerEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                ctx.BuyerEmailDomains.Add(domain);
            }
        }

        return ctx;
    }

    private bool IsAccountStatement(
        EmailMessage email,
        List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)> extractedInvoices,
        CompanyBillInboxSettings settings)
    {
        // 1. Any extracted document explicitly flagged as a statement by UniversalDocumentClassifier or PdfInvoiceInspectionService
        if (extractedInvoices.Any(x => x.Extraction.IsAccountStatement ||
                                       string.Equals(x.Extraction.RawDocumentType, "Statement", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string[] defaultStatementTerms =
        [
            "statement", "releve", "relevé", "etat_de_compte", "etat-de-compte", "kontoauszug", "extracto", "sommaire"
        ];

        // 2. Attachment filenames containing statement terms
        foreach (var (_, _, fileName) in extractedInvoices)
        {
            if (ContainsAnyKeyword(fileName, defaultStatementTerms) ||
                ContainsAnyKeyword(fileName, settings.StatementKeywords))
            {
                return true;
            }
        }

        foreach (var att in email.Attachments)
        {
            if (ContainsAnyKeyword(att.FileName, defaultStatementTerms) ||
                ContainsAnyKeyword(att.FileName, settings.StatementKeywords))
            {
                return true;
            }
        }

        // 3. If all extracted attachments are confident actionable invoices, do not suppress them based on email subject/body phrasing
        if (extractedInvoices.Any(x => x.Extraction.IsConfident && !x.Extraction.IsStatement && !x.Extraction.IsPaymentReceipt))
        {
            return false;
        }

        // 4. Automatic multilingual classification on email subject/body when no confident invoice PDF was extracted
        var (isStatement, _, _) = UniversalDocumentClassifier.ClassifyEmail(
            email.Subject,
            email.Snippet,
            extractedInvoices.Select(x => x.Extraction).ToList());
        if (isStatement)
        {
            return true;
        }

        var combinedText = $"{email.Subject} {email.Snippet}";
        if (ContainsAnyKeyword(combinedText, settings.StatementKeywords))
        {
            return true;
        }

        return false;
    }

    private bool IsPaymentReceiptEmail(
        EmailMessage email,
        List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)> extractedInvoices,
        CompanyBillInboxSettings settings)
    {
        // Never suppress actionable invoices that have a real invoice number and balance/terms
        if (extractedInvoices.Any(x => x.Extraction.IsConfident && !x.Extraction.IsPaymentReceipt && !x.Extraction.IsStatement))
        {
            return false;
        }

        if (extractedInvoices.Count > 0 && extractedInvoices.All(x => x.Extraction.IsPaymentReceipt))
        {
            return true;
        }

        var (_, isReceipt, _) = UniversalDocumentClassifier.ClassifyEmail(
            email.Subject,
            email.Snippet,
            extractedInvoices.Select(x => x.Extraction).ToList());
        if (isReceipt)
        {
            return true;
        }

        var combinedText = $"{email.Subject} {email.Snippet}";
        // Strip payment-terms phrases like "Due upon receipt" / "Payable à réception" before checking keywords
        var cleanedText = System.Text.RegularExpressions.Regex.Replace(
            combinedText,
            @"\b(?:due\s+(?:upon|on)\s+receipt|payable\s+(?:upon|on)\s+receipt|payable\s+[aà]\s+r[eé]ception)\b",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return ContainsAnyKeyword(cleanedText, settings.ReceiptKeywords);
    }

    internal async Task<EmailMessage?> ProcessMimeMessageAsync(MimeMessage mimeMessage, uint uid = 1, CancellationToken cancellationToken = default)
    {
        var email = new EmailMessage
        {
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FolderPath = CompanyBillInboxConstants.DefaultFolder,
            UniqueId = uid,
            GlobalMessageId = mimeMessage.MessageId ?? Guid.NewGuid().ToString(),
            Subject = mimeMessage.Subject ?? "No Subject",
            FromAddress = mimeMessage.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            FromName = mimeMessage.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            Date = mimeMessage.Date,
            Snippet = mimeMessage.TextBody?.Length > 100 ? mimeMessage.TextBody[..100] : (mimeMessage.TextBody ?? "")
        };
        var settings = GetSettings();
        var extractionContext = BuildExtractionContext(settings);

        // Cache raw MIME message so full HTML body with styling and inline attachments is viewable in email viewer
        if (_cacheMaintenance != null)
        {
            try
            {
                var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime");
                Directory.CreateDirectory(mimeFolder);
                var mimePath = Path.Combine(mimeFolder, $"{email.Id}.eml");
                await using var mimeStream = File.Create(mimePath);
                await mimeMessage.WriteToAsync(mimeStream, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to cache billing email MIME message {EmailId}", email.Id);
            }
        }

        // Check if sender matches ignored domains / spammers
        if (settings.IgnoredSenderDomains.Any(d => !string.IsNullOrWhiteSpace(d) &&
            (email.FromAddress.Contains(d, StringComparison.OrdinalIgnoreCase) || email.FromName.Contains(d, StringComparison.OrdinalIgnoreCase))))
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Dismissed;
            _db.EmailMessages.Save(email);
            await GenerateDecisionLogAsync(email);
            return email;
        }

        // Extract attachments
        var attachments = mimeMessage.Attachments.OfType<MimePart>().ToList();
        var extractedInvoices = new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>();
        var scannedAttachments = new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>();
        string? fallbackAttachmentPath = null;
        string? fallbackAttachmentName = null;

        var attachmentFolder = _cacheMaintenance != null
            ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Attachments", email.Id)
            : null;
        if (!string.IsNullOrEmpty(attachmentFolder) && attachments.Any())
        {
            try
            {
                Directory.CreateDirectory(attachmentFolder);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create attachment directory {Dir}", attachmentFolder);
            }
        }

        foreach (var part in attachments)
        {
            var fileName = part.FileName ?? "attachment";
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            using var mem = new MemoryStream();
            if (part.Content != null)
            {
                await part.Content.DecodeToAsync(mem, cancellationToken);
                mem.Position = 0;
            }

            // Save attachment file to Spokes storage
            var uploadBillId = Guid.NewGuid().ToString();
            var savedPath = await _fileService.UploadStreamAsync("bills", uploadBillId, mem, fileName);

            var attachmentId = Guid.NewGuid().ToString();

            // Also cache attachment in standard email cache for instant preview and download via EmailReader
            if (!string.IsNullOrEmpty(attachmentFolder))
            {
                try
                {
                    var cachedFilePath = Path.Combine(attachmentFolder, attachmentId);
                    mem.Position = 0;
                    await using var fileStream = File.Create(cachedFilePath);
                    await mem.CopyToAsync(fileStream, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to cache email attachment {AttachmentId} to email body path", attachmentId);
                }
            }

            email.Attachments.Add(new EmailAttachmentMeta
            {
                Id = attachmentId,
                FileName = fileName,
                ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                Size = mem.Length
            });
            email.HasAttachments = true;

            // Try extracting invoice details if PDF or XML
            if (ext == ".pdf" || ext == ".xml")
            {
                mem.Position = 0;
                var extracted = _pdfInspectionService.Inspect(mem, fileName, extractionContext);
                if (extracted != null)
                {
                    if (extracted.TotalAmount != 0)
                    {
                        extractedInvoices.Add((extracted, savedPath, fileName));
                    }
                    else if (extracted.IsScannedImagePdf || extracted.IsStatement || extracted.IsPaymentReceipt)
                    {
                        scannedAttachments.Add((extracted, savedPath, fileName));
                    }
                }
            }

            if (fallbackAttachmentPath == null && (ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg"))
            {
                fallbackAttachmentPath = savedPath;
                fallbackAttachmentName = fileName;
            }
        }

        email.MatchedAttachmentPath = extractedInvoices.Count > 0 ? extractedInvoices[0].SavedPath : fallbackAttachmentPath;
        email.MatchedAttachmentName = extractedInvoices.Count > 0 ? extractedInvoices[0].FileName : fallbackAttachmentName;

        if (extractedInvoices.Count > 0)
        {
            email.DetectedAmount = extractedInvoices[0].Extraction.TotalAmount;
            email.DetectedCurrency = extractedInvoices[0].Extraction.Currency;
            email.DetectedPaidAmount = extractedInvoices[0].Extraction.PaidAmount;
            email.DetectedBalanceDue = extractedInvoices[0].Extraction.BalanceDue;
        }
        else
        {
            var textAmount = ExtractAmountFromText($"{email.Subject} {email.Snippet}");
            if (textAmount > 0)
            {
                email.DetectedAmount = textAmount;
                email.DetectedCurrency = settings.DefaultCurrency;
            }
        }

        var allInspectedDocs = extractedInvoices.Concat(scannedAttachments).ToList();

        // Check for periodic statements or payment receipts before drafting
        if (IsAccountStatement(email, allInspectedDocs, settings))
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Statement;
            _db.EmailMessages.Save(email);
            await GenerateDecisionLogAsync(email, allInspectedDocs);
            return email;
        }

        if (IsPaymentReceiptEmail(email, allInspectedDocs, settings))
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt;
            _db.EmailMessages.Save(email);
            await GenerateDecisionLogAsync(email, allInspectedDocs);
            return email;
        }

        // Supplier matching
        var primaryExt = extractedInvoices.FirstOrDefault().Extraction;
        var supplier = FindSupplier(email.FromAddress, email.FromName, primaryExt?.SupplierName, primaryExt?.SupplierEmail, primaryExt?.SupplierTaxId);

        if (extractedInvoices.Count > 0)
        {
            var createdBills = new List<Bill>();
            bool hasDuplicate = false;
            var batchEvaluations = EvaluateExtractedInvoicesBatch(email, extractedInvoices, supplier, settings);

            foreach (var (extraction, path, fileName, itemSupplier, poMatch, existingMatch) in batchEvaluations)
            {
                if (existingMatch != null)
                {
                    hasDuplicate = true;
                    if (!email.LinkedBillIds.Contains(existingMatch.Bill.Id))
                    {
                        email.LinkedBillIds.Add(existingMatch.Bill.Id);
                    }
                    continue; // Skip creating a duplicate draft
                }

                var draftBill = BuildDraftBillFromExtraction(
                    email,
                    extraction,
                    itemSupplier,
                    poMatch,
                    path,
                    fileName,
                    settings,
                    autoIngested: true);

                _db.Bills.Save(draftBill);
                createdBills.Add(draftBill);
                if (!email.LinkedBillIds.Contains(draftBill.Id))
                {
                    email.LinkedBillIds.Add(draftBill.Id);
                }
            }

            if (createdBills.Count > 0)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                email.LinkedBillId = createdBills[0].Id;
                email.LinkedPoId = createdBills[0].PurchaseOrderId;
            }
            else if (hasDuplicate)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                email.LinkedBillId = email.LinkedBillIds.FirstOrDefault();
            }
        }
        else
        {
            bool handledByRecurring = false;
            if (settings.AutoMatchRecurringExpenses && settings.AllowDraftWithoutPdfForRecurring)
            {
                // Portal notification / recurring bill check without PDF
                var recurring = FindMatchingRecurringExpense(supplier?.Name ?? email.FromName);
                if (recurring != null)
                {
                    var parsedAmount = ExtractAmountFromText($"{email.Subject} {email.Snippet}");
                    if (parsedAmount > 0)
                    {
                        handledByRecurring = true;
                        // Check if bill already exists for this recurring expense / supplier in this period
                        var existingRecurringMatch = FindExistingBillMatch(
                            extraction: null,
                            supplierId: supplier?.Id,
                            supplierName: recurring.Supplier,
                            emailSubject: email.Subject,
                            emailSnippet: email.Snippet,
                            matchedPoId: null,
                            currentEmailId: email.Id,
                            duplicateWindowDays: settings.DuplicateDateWindowDays,
                            textDetectedAmount: parsedAmount);

                        if (existingRecurringMatch != null)
                        {
                            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                            email.LinkedBillId = existingRecurringMatch.Bill.Id;
                            email.LinkedBillIds = [existingRecurringMatch.Bill.Id];
                        }
                        else
                        {
                            var draftBill = new Bill
                            {
                                Id = Guid.NewGuid().ToString(),
                                SupplierId = supplier?.Id ?? string.Empty,
                                SupplierName = recurring.Supplier,
                                Description = $"Recurring: {recurring.Name} (Amount from email)",
                                Date = email.Date.DateTime,
                                DueDate = email.Date.DateTime.AddDays(30),
                                Amount = parsedAmount,
                                Currency = settings.DefaultCurrency,
                                LinkedRecurringExpenseId = recurring.Id,
                                Status = BillStatus.AwaitingApproval,
                                AttachmentPath = "",
                                AttachmentName = "",
                                AutoIngested = true,
                                SourceEmailId = email.Id,
                                SourceEmailSubject = email.Subject,
                                SourceSenderEmail = email.FromAddress,
                                AutoMatchReason = $"Matched to recurring expense template '{recurring.Name}'"
                            };

                            _db.Bills.Save(draftBill);
                            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                            email.LinkedBillId = draftBill.Id;
                            email.LinkedBillIds = [draftBill.Id];
                        }
                    }
                }
            }

            if (!handledByRecurring)
            {
                // Fallback for emails without extractable PDF/XML: check if email subject/amount matches an existing bill
                var existingFallbackMatch = FindExistingBillMatch(
                    extraction: null,
                    supplierId: supplier?.Id,
                    supplierName: supplier?.Name ?? email.FromName,
                    emailSubject: email.Subject,
                    emailSnippet: email.Snippet,
                    matchedPoId: null,
                    currentEmailId: email.Id,
                    duplicateWindowDays: settings.DuplicateDateWindowDays,
                    textDetectedAmount: email.DetectedAmount);

                if (existingFallbackMatch != null)
                {
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                    email.LinkedBillId = existingFallbackMatch.Bill.Id;
                    email.LinkedBillIds = [existingFallbackMatch.Bill.Id];
                }
                else
                {
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed;
                }
            }
        }

        _db.EmailMessages.Save(email);
        await GenerateDecisionLogAsync(email, allInspectedDocs);
        return email;
    }

    private Bill BuildDraftBillFromExtraction(
        EmailMessage email,
        ExtractedInvoiceData? extraction,
        Supplier? itemSupplier,
        MatchingPoResult poMatch,
        string attachmentPath,
        string attachmentName,
        CompanyBillInboxSettings settings,
        bool autoIngested = true)
    {
        bool isCredit = extraction != null &&
                        (extraction.IsCreditNote ||
                         extraction.TotalAmount < 0 ||
                         extraction.RawDocumentType is "381" or "261" or "81" ||
                         ContainsAnyKeyword($"{email.Subject} {extraction.InvoiceNumber}", settings.CreditNoteKeywords));

        if (isCredit && extraction != null && !extraction.IsCreditNote)
        {
            UniversalDocumentClassifier.ApplyCreditNoteNormalization(extraction, "Credit note keyword detected");
        }

        string? recurringId = null;
        if (settings.AutoMatchRecurringExpenses)
        {
            var rec = FindMatchingRecurringExpense(itemSupplier?.Name ?? extraction?.SupplierName ?? email.FromName);
            recurringId = rec?.Id;
        }

        decimal totalAmount = 0m;
        decimal subTotal = 0m;
        decimal taxAmount = 0m;
        decimal tpsAmount = 0m;
        decimal tvqAmount = 0m;

        if (extraction != null)
        {
            totalAmount = isCredit && extraction.TotalAmount > 0 ? -extraction.TotalAmount : extraction.TotalAmount;
            taxAmount = isCredit && extraction.TaxAmount > 0 ? -extraction.TaxAmount : extraction.TaxAmount;
            tpsAmount = isCredit && extraction.TpsAmount > 0 ? -extraction.TpsAmount : extraction.TpsAmount;
            tvqAmount = isCredit && extraction.TvqAmount > 0 ? -extraction.TvqAmount : extraction.TvqAmount;
            subTotal = extraction.SubTotal != 0
                ? (isCredit && extraction.SubTotal > 0 ? -extraction.SubTotal : extraction.SubTotal)
                : totalAmount - taxAmount;
        }

        var draftBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierId = itemSupplier?.Id ?? string.Empty,
            SupplierName = itemSupplier?.Name ?? extraction?.SupplierName ?? (!string.IsNullOrWhiteSpace(email.FromName) ? email.FromName : email.FromAddress),
            Description = extraction != null && !string.IsNullOrWhiteSpace(extraction.InvoiceNumber)
                ? (isCredit ? $"Credit Note {extraction.InvoiceNumber}" : $"Invoice {extraction.InvoiceNumber}")
                : (poMatch.MatchedPo != null ? $"Bill for {poMatch.MatchedPo.PoNumber}" : email.Subject),
            InvoiceNumber = extraction?.InvoiceNumber,
            Date = extraction?.InvoiceDate ?? email.Date.DateTime,
            DueDate = extraction?.DueDate ?? (extraction != null && extraction.PaidAmount > 0 && extraction.BalanceDue <= 0
                ? (extraction.InvoiceDate ?? email.Date.DateTime)
                : (extraction?.InvoiceDate ?? email.Date.DateTime).AddDays(30)),
            Amount = totalAmount,
            SubTotal = subTotal,
            TaxAmount = taxAmount,
            TpsAmount = tpsAmount,
            TvqAmount = tvqAmount,
            Currency = !string.IsNullOrWhiteSpace(extraction?.Currency) ? extraction!.Currency : settings.DefaultCurrency,
            IsCreditNote = isCredit,
            LinkedRecurringExpenseId = recurringId,
            Status = BillStatus.AwaitingApproval,
            PurchaseOrderId = poMatch.MatchedPo?.Id,
            AttachmentPath = attachmentPath,
            AttachmentName = attachmentName,
            AutoIngested = autoIngested,
            SourceEmailId = email.Id,
            SourceEmailSubject = email.Subject,
            SourceSenderEmail = email.FromAddress,
            AutoMatchReason = poMatch.Reason
        };

        if (extraction != null && extraction.LineItems.Count > 0)
        {
            foreach (var line in extraction.LineItems)
            {
                draftBill.LineItems.Add(new BillLineItem
                {
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice
                });
            }

            var linesSum = draftBill.LineItems.Sum(l => l.Total);
            if (extraction.DiscountAmount > 0 && Math.Abs(Math.Abs(linesSum) - Math.Abs(subTotal)) >= 0.02m)
            {
                draftBill.LineItems.Add(new BillLineItem
                {
                    Description = "Discount",
                    Quantity = 1m,
                    UnitPrice = isCredit ? Math.Abs(extraction.DiscountAmount) : -Math.Abs(extraction.DiscountAmount)
                });
            }

            if (extraction.ShippingAmount > 0 && Math.Abs(Math.Abs(draftBill.LineItems.Sum(l => l.Total)) - Math.Abs(subTotal)) >= 0.02m)
            {
                draftBill.LineItems.Add(new BillLineItem
                {
                    Description = "Shipping & Handling",
                    Quantity = 1m,
                    UnitPrice = isCredit ? -Math.Abs(extraction.ShippingAmount) : Math.Abs(extraction.ShippingAmount)
                });
            }
        }
        else if (poMatch.MatchedPo != null && poMatch.MatchedPo.LineItems.Count > 0)
        {
            foreach (var poItem in poMatch.MatchedPo.LineItems)
            {
                draftBill.LineItems.Add(new BillLineItem
                {
                    PoItemId = poItem.Id,
                    Description = poItem.Description,
                    Quantity = poItem.Quantity,
                    UnitPrice = poItem.UnitPrice
                });
            }
        }

        if (extraction != null && extraction.PaidAmount > 0)
        {
            bool isPaymentAlreadyRecorded = false;
            if (!string.IsNullOrWhiteSpace(draftBill.InvoiceNumber))
            {
                var matchingExistingBills = _db.Bills.GetAll()
                    .Where(b => b.Status != BillStatus.Void &&
                                b.Id != draftBill.Id &&
                                InvoiceNumbersMatch(b.InvoiceNumber, draftBill.InvoiceNumber) &&
                                (SupplierNamesMatch(b.SupplierName, draftBill.SupplierName) || string.IsNullOrEmpty(draftBill.SupplierName)))
                    .ToList();

                isPaymentAlreadyRecorded = matchingExistingBills.Any(b =>
                    b.Payments.Any(p => Math.Abs(p.Amount - extraction.PaidAmount) <= 0.05m) ||
                    Math.Abs(b.AmountPaid - extraction.PaidAmount) <= 0.05m);
            }

            if (!isPaymentAlreadyRecorded)
            {
                draftBill.Payments.Add(new PaymentRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Date = extraction.InvoiceDate ?? email.Date.DateTime,
                    Amount = extraction.PaidAmount,
                    Method = "Unknown",
                    Reference = !string.IsNullOrWhiteSpace(draftBill.InvoiceNumber) ? $"Payment on #{draftBill.InvoiceNumber}" : "Document payment",
                    Notes = "Prior payment detected on supplier document / reminder"
                });
            }
        }

        return draftBill;
    }

    private static string NormalizeTaxIdToken(string raw)
    {
        return new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private (Supplier? Supplier, string Method) FindSupplierWithMethod(
        string senderEmail,
        string senderName,
        string? extractedSupplierName,
        string? extractedSupplierEmail = null,
        string? extractedSupplierTaxId = null)
    {
        var allSuppliers = _db.Suppliers.GetAll();
        if (allSuppliers.Count == 0) return (null, "No registered suppliers in database");

        // 0. Match by extracted supplier Tax ID / VAT / GST / EIN (highest certainty)
        if (!string.IsNullOrWhiteSpace(extractedSupplierTaxId))
        {
            var extractedTokens = extractedSupplierTaxId
                .Split(['/', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeTaxIdToken)
                .Where(t => t.Length >= 6)
                .ToList();

            if (extractedTokens.Count > 0)
            {
                var matchByTaxId = allSuppliers.FirstOrDefault(s =>
                {
                    if (string.IsNullOrWhiteSpace(s.ContactInfo.TaxId)) return false;
                    var supplierTokens = s.ContactInfo.TaxId
                        .Split(['/', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(NormalizeTaxIdToken)
                        .Where(t => t.Length >= 6);
                    return supplierTokens.Any(st => extractedTokens.Any(et =>
                        st.Equals(et, StringComparison.OrdinalIgnoreCase) ||
                        (st.Length >= 8 && et.Length >= 8 &&
                         (st.Contains(et, StringComparison.OrdinalIgnoreCase) || et.Contains(st, StringComparison.OrdinalIgnoreCase)))));
                });

                if (matchByTaxId != null)
                {
                    return (matchByTaxId, $"Document Tax/VAT ID match '{extractedSupplierTaxId}'");
                }
            }
        }

        // 1. Match by extracted supplier email (from PDF header)
        if (!string.IsNullOrWhiteSpace(extractedSupplierEmail))
        {
            var matchByExtractedEmail = allSuppliers.FirstOrDefault(s =>
                s.Contacts.Any(c => c.Email.Equals(extractedSupplierEmail, StringComparison.OrdinalIgnoreCase)) ||
                s.ContactInfo.ContactPersonEmail.Equals(extractedSupplierEmail, StringComparison.OrdinalIgnoreCase));

            if (matchByExtractedEmail != null) return (matchByExtractedEmail, $"Document supplier email match '{extractedSupplierEmail}'");

            var atIdx = extractedSupplierEmail.IndexOf('@');
            if (atIdx > 0)
            {
                var docDomain = extractedSupplierEmail[(atIdx + 1)..];
                if (!IsCommonEmailDomain(docDomain))
                {
                    var matchDocDomain = allSuppliers.FirstOrDefault(s =>
                        s.ContactInfo.ContactPersonEmail.Contains(docDomain, StringComparison.OrdinalIgnoreCase) ||
                        s.Contacts.Any(c => c.Email.Contains(docDomain, StringComparison.OrdinalIgnoreCase)));
                    if (matchDocDomain != null) return (matchDocDomain, $"Document email domain match '@{docDomain}'");
                }
            }
        }

        // 2. Match by extracted supplier name
        if (!string.IsNullOrWhiteSpace(extractedSupplierName))
        {
            var cleanExtName = extractedSupplierName.Trim();
            var matchExtName = allSuppliers.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.Name) && (
                    s.Name.Trim().Equals(cleanExtName, StringComparison.OrdinalIgnoreCase) ||
                    (s.Name.Trim().Length >= 3 && cleanExtName.Length >= 3 &&
                     (s.Name.Trim().Contains(cleanExtName, StringComparison.OrdinalIgnoreCase) ||
                      cleanExtName.Contains(s.Name.Trim(), StringComparison.OrdinalIgnoreCase)))));

            if (matchExtName != null) return (matchExtName, $"Extracted company name match '{extractedSupplierName}'");
        }

        // 3. Match by sender email (unless sender is internal company address)
        var settings = GetSettings();
        var companyEmail = settings.EmailAddress ?? "";
        var companyDomain = companyEmail.Contains('@') ? companyEmail[(companyEmail.IndexOf('@') + 1)..] : "";

        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            var atIndex = senderEmail.IndexOf('@');
            var senderDomain = atIndex > 0 ? senderEmail[(atIndex + 1)..] : "";
            bool isInternalSender = !string.IsNullOrEmpty(senderDomain) &&
                                    (senderDomain.Equals(companyDomain, StringComparison.OrdinalIgnoreCase) ||
                                     senderDomain.Contains("poly-robotics", StringComparison.OrdinalIgnoreCase));

            if (!isInternalSender)
            {
                var matchByEmail = allSuppliers.FirstOrDefault(s =>
                    s.Contacts.Any(c => c.Email.Equals(senderEmail, StringComparison.OrdinalIgnoreCase)) ||
                    s.ContactInfo.ContactPersonEmail.Equals(senderEmail, StringComparison.OrdinalIgnoreCase));

                if (matchByEmail != null) return (matchByEmail, $"Exact email match '{senderEmail}'");

                if (!IsCommonEmailDomain(senderDomain))
                {
                    var domainToken = senderDomain.Split('.')[0];
                    var matchDomain = allSuppliers.FirstOrDefault(s =>
                        s.ContactInfo.ContactPersonEmail.Contains(senderDomain, StringComparison.OrdinalIgnoreCase) ||
                        s.Contacts.Any(c => c.Email.Contains(senderDomain, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(s.Name) && domainToken.Length >= 4 &&
                         (s.Name.Replace(" ", "").Replace("-", "").Contains(domainToken, StringComparison.OrdinalIgnoreCase) ||
                          domainToken.Contains(s.Name.Replace(" ", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase))));
                    if (matchDomain != null) return (matchDomain, $"Email domain match '@{senderDomain}'");
                }
            }
        }

        // 4. Match by sender display name
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            var cleanSenderName = senderName.Trim();
            var matchName = allSuppliers.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.Name) && (
                    s.Name.Trim().Equals(cleanSenderName, StringComparison.OrdinalIgnoreCase) ||
                    (s.Name.Trim().Length >= 3 && cleanSenderName.Length >= 3 &&
                     (s.Name.Trim().Contains(cleanSenderName, StringComparison.OrdinalIgnoreCase) ||
                      cleanSenderName.Contains(s.Name.Trim(), StringComparison.OrdinalIgnoreCase)))));

            if (matchName != null) return (matchName, $"Sender name match '{senderName}'");
        }

        return (null, "No match found");
    }

    private static bool IsCommonEmailDomain(string domain)
    {
        return domain.Contains("gmail") || domain.Contains("outlook") || domain.Contains("hotmail") || domain.Contains("yahoo") || domain.Contains("icloud");
    }

    private Supplier? FindSupplier(string senderEmail, string senderName, string? extractedSupplierName, string? extractedSupplierEmail = null, string? extractedSupplierTaxId = null)
    {
        return FindSupplierWithMethod(senderEmail, senderName, extractedSupplierName, extractedSupplierEmail, extractedSupplierTaxId).Supplier;
    }

    /// <summary>
    /// Evaluates the complete decision tree and field extraction diagnostics for an email on-demand.
    /// Provides full visibility into why an email was processed, rejected, or classified, and how each field was populated.
    /// </summary>
    public async Task<BillInboxDecisionLog> GenerateDecisionLogAsync(
        EmailMessage email,
        List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>? preExtractedInvoices = null)
    {
        var settings = GetSettings();
        var extractionContext = BuildExtractionContext(settings);
        var log = new BillInboxDecisionLog
        {
            EvaluatedAt = DateTime.UtcNow
        };

        // Step 1: Spam & Sender Filtering
        var ignoredDomain = settings.IgnoredSenderDomains.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) &&
            (email.FromAddress.Contains(d, StringComparison.OrdinalIgnoreCase) || email.FromName.Contains(d, StringComparison.OrdinalIgnoreCase)));

        if (ignoredDomain != null)
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "1",
                StepName = "Spam & Sender Filtering",
                Status = DecisionStepStatus.Warning,
                Outcome = $"Sender matched ignored filter '{ignoredDomain}'. Processing halted.",
                Details = [
                    $"Sender: {email.FromName} <{email.FromAddress}>",
                    $"Matched Ignored Pattern: {ignoredDomain}",
                    "Action: Email automatically classified as Dismissed."
                ]
            });
            log.FinalStatus = CompanyBillInboxConstants.BillingStatus.Dismissed;
            log.SummaryReason = $"Dismissed: Sender matches configured ignored filter '{ignoredDomain}'.";
            email.DecisionLog = log;
            _db.EmailMessages.Save(email);
            return log;
        }

        log.Steps.Add(new DecisionLogStep
        {
            StepNumber = "1",
            StepName = "Spam & Sender Filtering",
            Status = DecisionStepStatus.Success,
            Outcome = $"Sender '{email.FromAddress}' passed spam/ignored filtering.",
            Details = [
                $"Sender Address: {email.FromAddress}",
                $"Sender Display Name: {email.FromName}",
                $"Ignored Domains Filter Count: {settings.IgnoredSenderDomains.Count} pattern(s)"
            ]
        });

        // Step 2: Attachment & Content Extraction
        var allInspectedDocs = preExtractedInvoices != null && preExtractedInvoices.Count > 0
            ? new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>(preExtractedInvoices)
            : new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>();
        var step2Details = new List<string>
        {
            $"Attachments listed on email: {email.Attachments.Count}"
        };

        if (allInspectedDocs.Count > 0)
        {
            foreach (var (extracted, _, fileName) in allInspectedDocs)
            {
                if (extracted.IsScannedImagePdf)
                {
                    step2Details.Add($"File '{fileName}': Detected as a scanned image PDF without a machine-readable text layer ({extracted.ClassificationReason}).");
                }
                else
                {
                    var paymentSuffix = extracted.PaidAmount > 0 ? $", Paid: {extracted.PaidAmount:C2}, Balance Due: {extracted.BalanceDue:C2}" : "";
                    var dueStr = extracted.DueDate.HasValue
                        ? extracted.DueDate.Value.ToShortDateString()
                        : (extracted.PaidAmount > 0 && extracted.BalanceDue <= 0 ? "Paid on invoice date" : "Net 30 default");
                    step2Details.Add($"In-memory extraction for '{fileName}': Recognized as {extracted.Source}. Extracted Invoice #{extracted.InvoiceNumber ?? "N/A"}, Amount: {extracted.TotalAmount:C2} {extracted.Currency}, Subtotal: {extracted.SubTotal:C2}, Tax: {extracted.TaxAmount:C2}{paymentSuffix}, Date: {extracted.InvoiceDate:d}, Due: {dueStr}, Lines: {extracted.LineItems.Count}.");
                }
            }
        }
        else
        {
            // Check if raw MIME is cached
            MimeMessage? cachedMime = null;
            if (_cacheMaintenance != null)
            {
                var mimePath = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime", $"{email.Id}.eml");
                if (File.Exists(mimePath))
                {
                    try
                    {
                        using var stream = File.OpenRead(mimePath);
                        cachedMime = await MimeMessage.LoadAsync(stream);
                    }
                    catch { }
                }
            }

            if (cachedMime != null)
            {
                step2Details.Add("Raw MIME message loaded from local disk cache.");
                foreach (var part in cachedMime.Attachments.OfType<MimePart>())
                {
                    var fileName = part.FileName ?? "attachment";
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext == ".pdf" || ext == ".xml")
                    {
                        using var mem = new MemoryStream();
                        if (part.Content != null)
                        {
                            await part.Content.DecodeToAsync(mem);
                            mem.Position = 0;
                            var extracted = _pdfInspectionService.Inspect(mem, fileName, extractionContext);
                            if (extracted != null)
                            {
                                allInspectedDocs.Add((extracted, email.MatchedAttachmentPath ?? "", fileName));
                                if (extracted.IsScannedImagePdf)
                                {
                                    step2Details.Add($"File '{fileName}' ({ext}): Scanned image PDF without machine-readable text layer ({extracted.ClassificationReason}).");
                                }
                                else if (extracted.TotalAmount != 0)
                                {
                                    var paymentSuffix = extracted.PaidAmount > 0 ? $", Paid: {extracted.PaidAmount:C2}, Balance Due: {extracted.BalanceDue:C2}" : "";
                                    var dueStr = extracted.DueDate.HasValue
                                        ? extracted.DueDate.Value.ToShortDateString()
                                        : (extracted.PaidAmount > 0 && extracted.BalanceDue <= 0 ? "Paid on invoice date" : "Net 30 default");
                                    step2Details.Add($"File '{fileName}' ({ext}): Recognized as {extracted.Source}. Extracted Invoice #{extracted.InvoiceNumber ?? "N/A"}, Amount: {extracted.TotalAmount:C2} {extracted.Currency}, Subtotal: {extracted.SubTotal:C2}, Tax: {extracted.TaxAmount:C2}{paymentSuffix}, Date: {extracted.InvoiceDate:d}, Due: {dueStr}, Lines: {extracted.LineItems.Count}.");
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(email.MatchedAttachmentPath))
                {
                    try
                    {
                        var parts = email.MatchedAttachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5)
                        {
                            var category = parts[2];
                            var contextId = parts[3];
                            var fileName = parts[4];
                            var fileExt = Path.GetExtension(fileName).ToLowerInvariant();
                            if (fileExt == ".pdf" || fileExt == ".xml")
                            {
                                using var stream = _fileService.OpenRead(category, contextId, fileName);
                                if (stream != null)
                                {
                                    using var mem = new MemoryStream();
                                    await stream.CopyToAsync(mem);
                                    mem.Position = 0;
                                    var extracted = _pdfInspectionService.Inspect(mem, email.MatchedAttachmentName ?? fileName, extractionContext);
                                    if (extracted != null)
                                    {
                                        allInspectedDocs.Add((extracted, email.MatchedAttachmentPath, email.MatchedAttachmentName ?? fileName));
                                        if (extracted.IsScannedImagePdf)
                                        {
                                            step2Details.Add($"File '{email.MatchedAttachmentName ?? fileName}': Scanned image PDF without text layer ({extracted.ClassificationReason}).");
                                        }
                                        else if (extracted.TotalAmount != 0)
                                        {
                                            var paymentSuffix = extracted.PaidAmount > 0 ? $", Paid: {extracted.PaidAmount:C2}, Balance Due: {extracted.BalanceDue:C2}" : "";
                                            step2Details.Add($"Inspected attached file '{email.MatchedAttachmentName ?? fileName}' via {extracted.Source}: Invoice #{extracted.InvoiceNumber ?? "N/A"}, Amount: {extracted.TotalAmount:C2} {extracted.Currency}{paymentSuffix}.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        step2Details.Add($"Could not re-inspect attached file at '{email.MatchedAttachmentPath}': {ex.Message}");
                    }
                }
            }

            // Fallback: if no file was readable but email is already linked to a drafted bill, reconstruct from the bill
            if (allInspectedDocs.Count == 0 && !string.IsNullOrEmpty(email.LinkedBillId))
            {
                var linkedBill = _db.Bills.GetById(email.LinkedBillId);
                if (linkedBill != null && linkedBill.Amount != 0)
                {
                    var reconstructed = new ExtractedInvoiceData
                    {
                        SupplierName = linkedBill.SupplierName,
                        InvoiceNumber = linkedBill.InvoiceNumber,
                        InvoiceDate = linkedBill.Date,
                        DueDate = linkedBill.DueDate,
                        TotalAmount = linkedBill.Amount,
                        SubTotal = linkedBill.SubTotal,
                        TaxAmount = linkedBill.TaxAmount,
                        TpsAmount = linkedBill.TpsAmount,
                        TvqAmount = linkedBill.TvqAmount,
                        PaidAmount = linkedBill.AmountPaid,
                        BalanceDue = linkedBill.BalanceDue,
                        Currency = linkedBill.Currency,
                        IsCreditNote = linkedBill.IsCreditNote,
                        Source = InvoiceExtractionSource.PdfTextHeuristic,
                        LineItems = linkedBill.LineItems.Select(l => new ExtractedInvoiceLineItem
                        {
                            Description = l.Description,
                            Quantity = l.Quantity,
                            UnitPrice = l.UnitPrice,
                            Total = l.Total
                        }).ToList()
                    };
                    allInspectedDocs.Add((reconstructed, linkedBill.AttachmentPath ?? "", linkedBill.AttachmentName ?? "attachment.pdf"));
                    step2Details.Add($"Reconstructed extraction from linked Bill #{linkedBill.InvoiceNumber ?? linkedBill.Id} ({linkedBill.SupplierName}): Amount: {linkedBill.Amount:C2} {linkedBill.Currency}.");
                }
            }
        }

        var extractedInvoices = allInspectedDocs
            .Where(x => x.Extraction.TotalAmount != 0 && !x.Extraction.IsAccountStatement && !x.Extraction.IsPaymentReceipt)
            .ToList();

        var textAmount = email.DetectedAmount ?? ExtractAmountFromText($"{email.Subject} {email.Snippet}");
        if (extractedInvoices.Count > 0)
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "2",
                StepName = "Attachment & Content Extraction",
                Status = DecisionStepStatus.Success,
                Outcome = $"Successfully extracted {extractedInvoices.Count} invoice document(s). Detected Total: {extractedInvoices[0].Extraction.TotalAmount:C2} {extractedInvoices[0].Extraction.Currency}.",
                Details = step2Details
            });
        }
        else if (allInspectedDocs.Any(x => x.Extraction.IsScannedImagePdf))
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "2",
                StepName = "Attachment & Content Extraction",
                Status = DecisionStepStatus.Warning,
                Outcome = "Attached PDF appears to be a scanned image without a machine-readable text layer.",
                Details = step2Details
            });
        }
        else if (textAmount > 0)
        {
            step2Details.Add($"Parsed amount {textAmount:C2} {settings.DefaultCurrency} from email subject/body text regex.");
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "2",
                StepName = "Attachment & Content Extraction",
                Status = DecisionStepStatus.Info,
                Outcome = $"No PDF/XML invoice extracted. Parsed {textAmount:C2} from email text.",
                Details = step2Details
            });
        }
        else
        {
            step2Details.Add("No invoice number or monetary amount detected.");
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "2",
                StepName = "Attachment & Content Extraction",
                Status = DecisionStepStatus.Warning,
                Outcome = "No structured invoice or monetary amount found in attachments or body text.",
                Details = step2Details
            });
        }

        // Step 3: Automatic Document Classification
        var (emailIsStatement, emailIsReceipt, emailClassReason) = UniversalDocumentClassifier.ClassifyEmail(
            email.Subject,
            email.Snippet,
            allInspectedDocs.Select(x => x.Extraction).ToList());

        var isStatement = emailIsStatement || IsAccountStatement(email, allInspectedDocs, settings);
        var isReceipt = !isStatement && (emailIsReceipt || IsPaymentReceiptEmail(email, allInspectedDocs, settings));

        if (isStatement)
        {
            var reasonText = !string.IsNullOrWhiteSpace(emailClassReason)
                ? emailClassReason
                : "Detected statement of account pattern in email or attachment";
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "3",
                StepName = "Automatic Document Classification",
                Status = DecisionStepStatus.Warning,
                Outcome = $"Classified as Statement of Account ({reasonText}).",
                Details = [
                    $"Classification signal: {reasonText}",
                    "Reasoning: Statements summarize account balances or past transactions. Creating a draft bill would duplicate liabilities.",
                    "Action: Email flagged as Statement. Reviewer can click 'Draft Bill Anyway' or 'Mark Dealt With'."
                ]
            });
            log.FinalStatus = CompanyBillInboxConstants.BillingStatus.Statement;
            log.SummaryReason = $"Statement of Account: {reasonText}. Automated bill drafting suppressed.";
            email.DecisionLog = log;
            _db.EmailMessages.Save(email);
            return log;
        }

        if (isReceipt)
        {
            var reasonText = !string.IsNullOrWhiteSpace(emailClassReason)
                ? emailClassReason
                : "Detected payment confirmation or receipt pattern";
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "3",
                StepName = "Automatic Document Classification",
                Status = DecisionStepStatus.Warning,
                Outcome = $"Classified as Payment Receipt ({reasonText}).",
                Details = [
                    $"Classification signal: {reasonText}",
                    "Reasoning: Payment receipts confirm that a transaction was already paid, debited, or charged.",
                    "Action: Email flagged as Receipt. Reviewer can verify against bank reconciliation."
                ]
            });
            log.FinalStatus = CompanyBillInboxConstants.BillingStatus.Receipt;
            log.SummaryReason = $"Payment Receipt: {reasonText}. Automated bill drafting suppressed.";
            email.DecisionLog = log;
            _db.EmailMessages.Save(email);
            return log;
        }

        var creditDoc = extractedInvoices.FirstOrDefault(x => x.Extraction.IsCreditNote || x.Extraction.TotalAmount < 0);
        log.Steps.Add(new DecisionLogStep
        {
            StepNumber = "3",
            StepName = "Automatic Document Classification",
            Status = DecisionStepStatus.Success,
            Outcome = creditDoc.Extraction != null
                ? $"Document classified as actionable Supplier Credit Note ({creditDoc.Extraction.ClassificationReason ?? "negative balance / credit header"})."
                : "Document classified as actionable supplier invoice.",
            Details = [
                creditDoc.Extraction != null
                    ? $"Credit Note signal: {creditDoc.Extraction.ClassificationReason ?? "Credit Note header or negative amount"}"
                    : "Evaluated multilingual structural & header classification rules (Invoice, Credit Note, Statement, Payment Receipt).",
                "No statement or payment-receipt suppression rules triggered."
            ]
        });

        // Step 4: Supplier Identification
        var primaryExt = extractedInvoices.FirstOrDefault().Extraction ?? allInspectedDocs.FirstOrDefault().Extraction;
        var (supplier, matchMethod) = FindSupplierWithMethod(email.FromAddress, email.FromName, primaryExt?.SupplierName, primaryExt?.SupplierEmail, primaryExt?.SupplierTaxId);
        if (supplier != null)
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "4",
                StepName = "Supplier Identification",
                Status = DecisionStepStatus.Success,
                Outcome = $"Matched registered supplier '{supplier.Name}' (ID: {supplier.Id}) via {matchMethod}.",
                Details = [
                    $"Supplier Name: {supplier.Name}",
                    $"Supplier ID: {supplier.Id}",
                    $"Match Strategy: {matchMethod}"
                ]
            });
        }
        else
        {
            var fallbackName = !string.IsNullOrWhiteSpace(primaryExt?.SupplierName)
                ? primaryExt.SupplierName
                : (!string.IsNullOrWhiteSpace(email.FromName) ? email.FromName : email.FromAddress);

            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "4",
                StepName = "Supplier Identification",
                Status = DecisionStepStatus.Warning,
                Outcome = $"No registered supplier matched in Spokes database. Fallback name: '{fallbackName}'.",
                Details = [
                    $"Checked extracted document Tax/VAT ID: {primaryExt?.SupplierTaxId ?? "None"}",
                    $"Checked extracted document supplier email: {primaryExt?.SupplierEmail ?? "None"}",
                    $"Checked extracted document supplier name: {primaryExt?.SupplierName ?? "None"}",
                    $"Checked sender email: {email.FromAddress}",
                    $"Checked sender display name: {email.FromName}",
                    "Result: No registered supplier matched. Reviewer can create a supplier from this bill later."
                ]
            });
        }

        // Step 5: Purchase Order Matching
        var primaryExtraction = extractedInvoices.FirstOrDefault().Extraction;
        var poMatch = _poMatchingEngine.FindMatch(primaryExtraction, supplier?.Id);
        if (poMatch.MatchedPo != null)
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "5",
                StepName = "Purchase Order Matching",
                Status = DecisionStepStatus.Success,
                Outcome = $"Matched to open PO #{poMatch.MatchedPo.PoNumber} ({poMatch.Reason}).",
                Details = [
                    $"Matched PO Number: {poMatch.MatchedPo.PoNumber}",
                    $"PO Total: {poMatch.MatchedPo.GrandTotal:C2}",
                    $"PO Status: {poMatch.MatchedPo.Status}",
                    $"Match Rationale: {poMatch.Reason}"
                ]
            });
        }
        else
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "5",
                StepName = "Purchase Order Matching",
                Status = DecisionStepStatus.Info,
                Outcome = "No open Purchase Order matched.",
                Details = [
                    $"PO tolerance window: ±{settings.PoMatchToleranceAmount:C2}",
                    "No open PO matched by PO number, item line match, or tolerance threshold."
                ]
            });
        }

        // Step 6: Duplicate Bill Detection
        var duplicateMatches = new List<(ExtractedInvoiceData Extraction, string FileName, Bill Bill, string Reason)>();
        var nonDuplicateExtractions = new List<(ExtractedInvoiceData Extraction, string FileName)>();
        var duplicateDetails = new List<string>
        {
            $"Duplicate date window checked: {settings.DuplicateDateWindowDays} days (+ same-supplier total amount check)"
        };

        if (extractedInvoices.Count > 0)
        {
            var batchEvaluations = EvaluateExtractedInvoicesBatch(email, extractedInvoices, supplier, settings);
            foreach (var (ext, path, file, _, _, match) in batchEvaluations)
            {
                if (match != null)
                {
                    duplicateMatches.Add((ext, file, match.Bill, match.MatchReason));
                    log.DuplicateMatches.Add(new DuplicateBillMatchSummary
                    {
                        ExistingBillId = match.Bill.Id,
                        AttachmentFileName = file,
                        AttachmentPath = path,
                        ExtractedInvoiceNumber = ext.InvoiceNumber,
                        ExtractedAmount = ext.TotalAmount,
                        ExtractedCurrency = ext.Currency,
                        ExtractedInvoiceDate = ext.InvoiceDate,
                        MatchReason = match.MatchReason,
                        MatchMethod = match.Method,
                        IsPossibleDuplicate = match.IsPossibleDuplicate
                    });

                    var isPaymentRecorded = ext.PaidAmount > 0 &&
                        (match.Bill.Payments.Any(p => Math.Abs(p.Amount - ext.PaidAmount) <= 0.05m) ||
                         Math.Abs(match.Bill.AmountPaid - ext.PaidAmount) <= 0.05m ||
                         (match.Bill.Status == BillStatus.Paid && ext.PaidAmount >= match.Bill.Amount));

                    var payWarn = ext.PaidAmount > 0
                        ? (isPaymentRecorded ? $" [Payment of {ext.PaidAmount:C2} already recorded]" : $" [WARNING: Unrecorded payment of {ext.PaidAmount:C2}!]")
                        : "";

                    duplicateDetails.Add($"• File '{file}': Matched existing bill #{match.Bill.InvoiceNumber ?? "N/A"} (ID: '{match.Bill.Id}', {match.Bill.Amount:C2}, Status: {match.Bill.Status}){payWarn} via {match.MatchReason}.");
                }
                else
                {
                    nonDuplicateExtractions.Add((ext, file));
                    duplicateDetails.Add($"• File '{file}': No duplicate found. Actionable invoice #{ext.InvoiceNumber ?? "N/A"} ({ext.TotalAmount:C2} {ext.Currency}).");
                }
            }
        }
        else
        {
            var textMatch = FindExistingBillMatch(
                extraction: null,
                supplierId: supplier?.Id,
                supplierName: supplier?.Name ?? email.FromName,
                emailSubject: email.Subject,
                emailSnippet: email.Snippet,
                matchedPoId: poMatch.MatchedPo?.Id,
                currentEmailId: email.Id,
                duplicateWindowDays: settings.DuplicateDateWindowDays,
                textDetectedAmount: textAmount);

            if (textMatch != null)
            {
                duplicateMatches.Add((new ExtractedInvoiceData { TotalAmount = textAmount }, "email_body", textMatch.Bill, textMatch.MatchReason));
                log.DuplicateMatches.Add(new DuplicateBillMatchSummary
                {
                    ExistingBillId = textMatch.Bill.Id,
                    AttachmentFileName = email.MatchedAttachmentName ?? "email_body",
                    AttachmentPath = email.MatchedAttachmentPath ?? string.Empty,
                    ExtractedAmount = textAmount > 0 ? textAmount : null,
                    ExtractedCurrency = email.DetectedCurrency ?? settings.DefaultCurrency,
                    MatchReason = textMatch.MatchReason,
                    MatchMethod = textMatch.Method,
                    IsPossibleDuplicate = textMatch.IsPossibleDuplicate
                });
                duplicateDetails.Add($"• Email text: Matched existing bill #{textMatch.Bill.InvoiceNumber ?? "N/A"} ({textMatch.Bill.Amount:C2}, {textMatch.Bill.Status}) via {textMatch.MatchReason}.");
            }
        }

        if (duplicateMatches.Count > 0)
        {
            var hasUnapprovedDrafts = nonDuplicateExtractions.Count > 0 ||
                email.LinkedBillIds.Select(id => _db.Bills.GetById(id)).Any(b => b != null && b.Status == BillStatus.AwaitingApproval && b.SourceEmailId == email.Id);

            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "6",
                StepName = "Duplicate Bill Detection",
                Status = hasUnapprovedDrafts ? DecisionStepStatus.Info : DecisionStepStatus.Warning,
                Outcome = duplicateMatches.Count == 1 && nonDuplicateExtractions.Count == 0
                    ? $"Duplicate bill detected: Existing bill ID '{duplicateMatches[0].Bill.Id}' (#{duplicateMatches[0].Bill.InvoiceNumber ?? "N/A"}, {duplicateMatches[0].Bill.Amount:C2})."
                    : (hasUnapprovedDrafts
                        ? $"{duplicateMatches.Count} duplicate bill(s) recognized; {nonDuplicateExtractions.Count} new bill draft(s) created."
                        : $"All {duplicateMatches.Count} invoice document(s) matched existing bills in Spokes."),
                Details = duplicateDetails
            });
        }
        else
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "6",
                StepName = "Duplicate Bill Detection",
                Status = DecisionStepStatus.Success,
                Outcome = "No duplicate bill found in Spokes database.",
                Details = duplicateDetails
            });
        }

        // Step 7: Recurring Expense Matching
        var recSupplier = supplier?.Name ?? primaryExtraction?.SupplierName ?? email.FromName;
        var recurring = settings.AutoMatchRecurringExpenses ? FindMatchingRecurringExpense(recSupplier) : null;
        if (recurring != null)
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "7",
                StepName = "Recurring Expense Matching",
                Status = DecisionStepStatus.Success,
                Outcome = $"Matched recurring expense template '{recurring.Name}'.",
                Details = [
                    $"Template Name: {recurring.Name}",
                    $"Template Supplier: {recurring.Supplier}",
                    $"Expected Frequency: {recurring.Frequency}"
                ]
            });
        }
        else
        {
            log.Steps.Add(new DecisionLogStep
            {
                StepNumber = "7",
                StepName = "Recurring Expense Matching",
                Status = DecisionStepStatus.Info,
                Outcome = "No recurring expense template matched.",
                Details = [
                    $"Evaluated recurring templates for supplier candidate: '{recSupplier}'"
                ]
            });
        }

        // Step 8: Final Decision & Field Population
        var linkedDrafts = email.LinkedBillIds
            .Select(id => _db.Bills.GetById(id))
            .Where(b => b != null && b.Status == BillStatus.AwaitingApproval && b.SourceEmailId == email.Id)
            .ToList();

        string finalStatus;
        if (linkedDrafts.Count > 0 || (nonDuplicateExtractions.Count > 0 && email.BillingStatus != CompanyBillInboxConstants.BillingStatus.Approved && email.BillingStatus != CompanyBillInboxConstants.BillingStatus.Dismissed))
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
        }
        else if (duplicateMatches.Count > 0 && (extractedInvoices.Count == 0 || duplicateMatches.Count >= extractedInvoices.Count))
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
        }
        else if (extractedInvoices.Count > 0)
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
        }
        else if (recurring != null && settings.AllowDraftWithoutPdfForRecurring && textAmount > 0)
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
        }
        else if (email.BillingStatus == CompanyBillInboxConstants.BillingStatus.Approved)
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.Approved;
        }
        else if (email.BillingStatus == CompanyBillInboxConstants.BillingStatus.Dismissed)
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.Dismissed;
        }
        else
        {
            finalStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed;
        }
        log.FinalStatus = finalStatus;

        if (finalStatus == CompanyBillInboxConstants.BillingStatus.AutoDrafted)
        {
            var draftsCount = Math.Max(linkedDrafts.Count, nonDuplicateExtractions.Count);
            var dupNote = duplicateMatches.Count > 0 ? $", {duplicateMatches.Count} existing bill(s) recognized" : "";
            var paymentNote = primaryExtraction?.PaidAmount > 0
                ? $" (including detected payment of {primaryExtraction.PaidAmount:C2}; balance due {primaryExtraction.BalanceDue:C2})"
                : "";
            var docLabel = primaryExtraction?.IsCreditNote == true ? "credit note" : "bill";
            log.SummaryReason = draftsCount > 1
                ? $"Auto-drafted {draftsCount} bills created{dupNote}. Awaiting user approval."
                : $"Auto-drafted {docLabel} created with extracted total {(email.DetectedAmount ?? primaryExtraction?.TotalAmount ?? 0):C2}{dupNote}{paymentNote}. Awaiting user approval.";
        }
        else if (finalStatus == CompanyBillInboxConstants.BillingStatus.DuplicateDetected)
        {
            log.SummaryReason = duplicateMatches.Count > 1
                ? $"Duplicate detected: All {duplicateMatches.Count} incoming invoices already entered in Spokes."
                : $"Duplicate detected: Matching bill #{duplicateMatches.FirstOrDefault().Bill?.InvoiceNumber ?? "N/A"} already entered.";
        }
        else if (finalStatus == CompanyBillInboxConstants.BillingStatus.Approved)
        {
            log.SummaryReason = "Dealt with: Draft bill was reviewed and approved into Accounts Payable.";
        }
        else
        {
            var scannedDoc = allInspectedDocs.FirstOrDefault(x => x.Extraction.IsScannedImagePdf);
            if (scannedDoc.Extraction != null)
            {
                log.SummaryReason = $"Scanned image PDF ('{scannedDoc.FileName}') has no machine-readable text layer. Review visually and enter amount manually.";
            }
            else if (email.Attachments.Count == 0)
            {
                log.SummaryReason = "No PDF/XML invoice attachment or monetary amount detected in email.";
            }
            else
            {
                log.SummaryReason = "No structured invoice total detected in attachments. Review visually to create a bill or dismiss.";
            }
        }

        // Field Sources Table
        var suppVal = supplier?.Name ?? primaryExtraction?.SupplierName ?? (!string.IsNullOrWhiteSpace(email.FromName) ? email.FromName : email.FromAddress);
        var suppSrc = supplier != null ? $"Spokes Supplier Directory ({matchMethod})" : (!string.IsNullOrWhiteSpace(primaryExtraction?.SupplierName) ? "Extracted from PDF Header" : "Email Sender Name");
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Supplier", Value = suppVal, Source = suppSrc, ExtractionMethod = supplier != null ? "Database match" : "Header fallback" });

        var invVal = primaryExtraction?.InvoiceNumber ?? "None";
        var invSrc = primaryExtraction != null ? $"{primaryExtraction.Source}" : "Not found in document";
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Invoice #", Value = invVal, Source = invSrc, ExtractionMethod = primaryExtraction != null ? (primaryExtraction.Source == InvoiceExtractionSource.FacturX_CII || primaryExtraction.Source == InvoiceExtractionSource.UBL ? "Embedded XML" : "PDF text heuristic regex") : "None" });

        var dateVal = primaryExtraction?.InvoiceDate?.ToShortDateString() ?? email.Date.ToLocalTime().ToString("d");
        var dateSrc = primaryExtraction?.InvoiceDate != null ? "Extracted from invoice" : "Email received date";
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Invoice Date", Value = dateVal, Source = dateSrc, ExtractionMethod = primaryExtraction?.InvoiceDate != null ? "Document date parser" : "IMAP header" });

        bool isPaidInFull = primaryExtraction?.PaidAmount > 0 && primaryExtraction.BalanceDue <= 0;
        var dueVal = primaryExtraction?.DueDate?.ToShortDateString()
                     ?? (isPaidInFull ? dateVal : (primaryExtraction?.InvoiceDate ?? email.Date.DateTime).AddDays(30).ToShortDateString());
        var dueSrc = primaryExtraction?.DueDate != null
            ? "Extracted from invoice"
            : (isPaidInFull ? "Paid in full on invoice (Balance $0.00)" : "Net 30 fallback calculation");
        var dueMethod = primaryExtraction?.DueDate != null
            ? "Document date parser"
            : (isPaidInFull ? "Document payment status" : "Rule (InvoiceDate + 30 days)");
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Due Date", Value = dueVal, Source = dueSrc, ExtractionMethod = dueMethod });

        var amtVal = (email.DetectedAmount ?? primaryExtraction?.TotalAmount ?? textAmount).ToString("C2");
        var amtSrc = primaryExtraction != null ? $"{primaryExtraction.Source}" : (textAmount > 0 ? "Email text regex" : "None");
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Total Amount", Value = amtVal, Source = amtSrc, ExtractionMethod = primaryExtraction != null ? "Document totals inspection" : "Regex ($/CAD pattern)" });

        if (primaryExtraction?.PaidAmount > 0)
        {
            log.FieldSources.Add(new DecisionFieldSource
            {
                FieldName = "Prior Payment",
                Value = primaryExtraction.PaidAmount.ToString("C2"),
                Source = $"{primaryExtraction.Source}",
                ExtractionMethod = "Document payment inspection"
            });
            log.FieldSources.Add(new DecisionFieldSource
            {
                FieldName = "Balance Due",
                Value = primaryExtraction.BalanceDue.ToString("C2"),
                Source = $"{primaryExtraction.Source}",
                ExtractionMethod = "Document totals inspection"
            });
        }

        var currVal = primaryExtraction?.Currency ?? email.DetectedCurrency ?? settings.DefaultCurrency;
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Currency", Value = currVal, Source = primaryExtraction?.Currency != null ? "Extracted from invoice" : "Default setting", ExtractionMethod = "Currency parser" });

        var poVal = poMatch.MatchedPo?.PoNumber ?? "None";
        var poSrc = poMatch.MatchedPo != null ? $"Smart PO Engine ({poMatch.Reason})" : "No PO matched";
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Purchase Order", Value = poVal, Source = poSrc, ExtractionMethod = poMatch.MatchedPo != null ? "PO matching engine" : "None" });

        var recVal = recurring?.Name ?? "None";
        var recSrc = recurring != null ? "Matched recurring template" : "None";
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Recurring Template", Value = recVal, Source = recSrc, ExtractionMethod = recurring != null ? "Supplier name match" : "None" });

        var attVal = email.MatchedAttachmentName ?? (extractedInvoices.Count > 0 ? extractedInvoices[0].FileName : (email.Attachments.Count > 0 ? email.Attachments[0].FileName : "None"));
        var attSrc = !string.IsNullOrEmpty(email.MatchedAttachmentPath) ? email.MatchedAttachmentPath : "No file attached";
        log.FieldSources.Add(new DecisionFieldSource { FieldName = "Primary Attachment", Value = attVal, Source = attSrc, ExtractionMethod = "Attachment inspection" });

        var step8Details = new List<string>
        {
            $"Billing Status assigned: {finalStatus}",
            $"Fields Populated: {log.FieldSources.Count} fields mapped from extraction sources."
        };
        if (primaryExtraction?.PaidAmount > 0)
        {
            step8Details.Add($"Prior payment of {primaryExtraction.PaidAmount:C2} detected on document. Pre-recorded payment against bill draft (Remaining Balance Due: {primaryExtraction.BalanceDue:C2}).");
        }

        log.Steps.Add(new DecisionLogStep
        {
            StepNumber = "8",
            StepName = "Final Decision & Field Population",
            Status = finalStatus == CompanyBillInboxConstants.BillingStatus.AutoDrafted || finalStatus == CompanyBillInboxConstants.BillingStatus.Approved ? DecisionStepStatus.Success : DecisionStepStatus.Info,
            Outcome = $"Final Status: {finalStatus}. {log.SummaryReason}",
            Details = step8Details
        });

        // Persist on email
        email.DecisionLog = log;
        _db.EmailMessages.Save(email);
        return log;
    }

    /// <summary>
    /// Approves an auto-drafted bill and marks the source email as dealt with.
    /// </summary>
    public void ApproveBillFromEmail(string emailMessageId, string billId, string userId)
    {
        var bill = _db.Bills.GetById(billId);
        if (bill != null)
        {
            if (bill.BalanceDue <= 0)
            {
                bill.Status = BillStatus.Paid;
                bill.DatePaid ??= bill.Payments.LastOrDefault()?.Date ?? DateTime.Today;
            }
            else
            {
                bill.Status = BillStatus.Unpaid; // Approved and awaiting payment of remaining balance
            }
            _db.Bills.Save(bill);
        }

        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            var remainingUnapprovedBills = email.LinkedBillIds
                .Select(id => _db.Bills.GetById(id))
                .Where(b => b != null && b.Id != billId && b.Status == BillStatus.AwaitingApproval)
                .ToList();

            if (remainingUnapprovedBills.Count == 0)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
                email.ProcessedByUserId = userId;
                email.ProcessedAt = DateTime.UtcNow;
            }
            else
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                email.LinkedBillId = remainingUnapprovedBills[0]!.Id;
            }
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Rejects and discards a draft bill from an incoming email.
    /// If marked as a duplicate of an existing bill, links the email to that bill and sets status to DuplicateDetected.
    /// If no other drafts remain and not a duplicate, marks the email as Dismissed.
    /// </summary>
    public void RejectDraftBillFromEmail(string emailMessageId, string billId, string userId, RejectDraftBillOptions? options = null)
    {
        options ??= new RejectDraftBillOptions();

        var bill = _db.Bills.GetById(billId);
        if (bill != null)
        {
            if (bill.Status != BillStatus.AwaitingApproval)
            {
                throw new InvalidOperationException($"Cannot reject bill '{billId}' with status '{bill.Status}'. Only draft bills awaiting approval can be rejected from the inbox.");
            }

            var poId = bill.PurchaseOrderId;
            _db.Bills.Delete(bill.Id);
            _logger?.LogInformation("Discarded draft bill {BillId} from email {EmailId}", billId, emailMessageId);

            if (!string.IsNullOrEmpty(poId))
            {
                var po = _db.PurchaseOrders.GetById(poId);
                if (po != null)
                {
                    var linkedBills = _db.Bills.GetByPo(po.Id);
                    po.TotalBilled = linkedBills.Where(b => b.Status != BillStatus.Void).Sum(b => b.Amount);
                    if (po.Status != PurchaseOrderStatus.Void && po.Status != PurchaseOrderStatus.Draft)
                    {
                        if (po.TotalBilled >= po.GrandTotal && po.GrandTotal > 0)
                        {
                            po.Status = PurchaseOrderStatus.FullyBilled;
                        }
                        else if (po.TotalBilled > 0)
                        {
                            po.Status = PurchaseOrderStatus.PartiallyBilled;
                        }
                        else if (po.Status == PurchaseOrderStatus.FullyBilled || po.Status == PurchaseOrderStatus.PartiallyBilled)
                        {
                            po.Status = PurchaseOrderStatus.Final;
                        }
                    }
                    _db.PurchaseOrders.Save(po);
                }
            }
        }

        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            email.LinkedBillIds.Remove(billId);

            Bill? duplicateBill = null;
            if (options.IsDuplicate && !string.IsNullOrEmpty(options.DuplicateOfBillId))
            {
                duplicateBill = _db.Bills.GetById(options.DuplicateOfBillId);
                if (duplicateBill != null)
                {
                    if (!email.LinkedBillIds.Contains(duplicateBill.Id))
                    {
                        email.LinkedBillIds.Add(duplicateBill.Id);
                    }

                    if (options.AttachDocumentToExistingBill && !string.IsNullOrEmpty(email.MatchedAttachmentPath))
                    {
                        if (string.IsNullOrEmpty(duplicateBill.AttachmentPath))
                        {
                            duplicateBill.AttachmentPath = email.MatchedAttachmentPath;
                            duplicateBill.AttachmentName = !string.IsNullOrEmpty(email.MatchedAttachmentName)
                                ? email.MatchedAttachmentName
                                : Path.GetFileName(email.MatchedAttachmentPath);
                            _db.Bills.Save(duplicateBill);
                        }
                    }
                }
            }

            var remainingDrafts = email.LinkedBillIds
                .Select(id => _db.Bills.GetById(id))
                .Where(b => b != null && b.Id != billId && b.Status == BillStatus.AwaitingApproval && b.SourceEmailId == email.Id)
                .ToList();

            if (remainingDrafts.Count > 0)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                email.LinkedBillId = remainingDrafts[0]!.Id;
            }
            else if (duplicateBill != null)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                email.LinkedBillId = duplicateBill.Id;
                email.ProcessedByUserId = userId;
                email.ProcessedAt = DateTime.UtcNow;
            }
            else
            {
                var remainingActiveLinkedBills = email.LinkedBillIds
                    .Select(id => _db.Bills.GetById(id))
                    .Where(b => b != null && b.Id != billId)
                    .ToList();

                if (remainingActiveLinkedBills.Count > 0)
                {
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                    email.LinkedBillId = remainingActiveLinkedBills[0]!.Id;
                    email.ProcessedByUserId = userId;
                    email.ProcessedAt = DateTime.UtcNow;
                }
                else
                {
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Dismissed;
                    email.LinkedBillId = null;
                    email.ProcessedByUserId = userId;
                    email.ProcessedAt = DateTime.UtcNow;
                }
            }

            // Update DecisionLog summary if available
            if (email.DecisionLog != null)
            {
                var actionDesc = duplicateBill != null
                    ? $"Draft rejected by user: duplicate of bill #{duplicateBill.InvoiceNumber ?? duplicateBill.Id} ({duplicateBill.SupplierName})."
                    : $"Draft rejected by user: {(!string.IsNullOrWhiteSpace(options.RejectionReason) ? options.RejectionReason : "Draft discarded")}.";
                email.DecisionLog.SummaryReason = actionDesc;
            }

            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Records a detected prior payment on an existing bill (e.g. from an incoming reminder or statement)
    /// and marks the email as approved/dealt with.
    /// </summary>
    public void RecordPaymentOnExistingBill(string emailMessageId, string billId, decimal paymentAmount, string userId)
    {
        var bill = _db.Bills.GetById(billId);
        var email = _db.EmailMessages.GetById(emailMessageId);

        if (bill != null && paymentAmount > 0)
        {
            if (!bill.Payments.Any(p => Math.Abs(p.Amount - paymentAmount) <= 0.05m))
            {
                var payment = new PaymentRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Date = email?.Date.DateTime ?? DateTime.Today,
                    Amount = paymentAmount,
                    Method = "Unknown",
                    Reference = !string.IsNullOrWhiteSpace(bill.InvoiceNumber) ? $"Payment on #{bill.InvoiceNumber}" : "Reminder payment",
                    Notes = "Prior payment detected from reminder/statement document"
                };
                bill.Payments.Add(payment);

                if (bill.BalanceDue <= 0)
                {
                    bill.Status = BillStatus.Paid;
                    bill.DatePaid = payment.Date;
                }
                else if (bill.Status == BillStatus.AwaitingApproval)
                {
                    bill.Status = BillStatus.Unpaid;
                }

                _db.Bills.Save(bill);
            }
        }

        if (email != null)
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            if (!email.LinkedBillIds.Contains(billId))
            {
                email.LinkedBillIds.Add(billId);
            }
            email.LinkedBillId = billId;
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Reopens an existing bill that was previously marked as Paid, because the payment failed in reality
    /// (e.g. card/bank decline) and the supplier has re-sent the bill or reminder.
    /// Reverts the bill to Unpaid, clears DatePaid, removes/voids the failed payment so BalanceDue accurately reflects
    /// the remaining liability, optionally attaches the reminder PDF, and marks the email as dealt with.
    /// </summary>
    public void ReopenBillForFailedPayment(string emailMessageId, string billId, string userId)
    {
        var bill = _db.Bills.GetById(billId);
        var email = _db.EmailMessages.GetById(emailMessageId);

        if (bill != null)
        {
            bill.Status = BillStatus.Unpaid;
            bill.DatePaid = null;

            if (bill.Payments.Count > 0)
            {
                var lastPayment = bill.Payments.Last();
                lastPayment.Notes = string.IsNullOrWhiteSpace(lastPayment.Notes)
                    ? "Payment failed - reminder received from supplier"
                    : $"{lastPayment.Notes} (FAILED - reminder received from supplier)";

                bill.Payments.Remove(lastPayment);
            }

            if (!string.IsNullOrEmpty(email?.MatchedAttachmentPath) && string.IsNullOrEmpty(bill.AttachmentPath))
            {
                bill.AttachmentPath = email.MatchedAttachmentPath;
                bill.AttachmentName = !string.IsNullOrEmpty(email.MatchedAttachmentName)
                    ? email.MatchedAttachmentName
                    : "Reminder_Invoice.pdf";
            }

            _db.Bills.Save(bill);
        }

        if (email != null)
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            if (!email.LinkedBillIds.Contains(billId))
            {
                email.LinkedBillIds.Add(billId);
            }
            email.LinkedBillId = billId;
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Manually updates an email's billing status (e.g. marked as processed or dismissed).
    /// </summary>
    public void UpdateEmailBillingStatus(string emailMessageId, string status, string userId, string? linkedBillId = null)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            email.BillingStatus = status;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(linkedBillId))
            {
                email.LinkedBillId = linkedBillId;
                if (!email.LinkedBillIds.Contains(linkedBillId))
                {
                    email.LinkedBillIds.Add(linkedBillId);
                }
            }
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Explicitly confirms that an email is a statement of account, recording the human review decision.
    /// </summary>
    public void ConfirmStatement(string emailMessageId, string userId, string? linkedBillId = null)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.StatementConfirmed;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(linkedBillId))
            {
                email.LinkedBillId = linkedBillId;
                if (!email.LinkedBillIds.Contains(linkedBillId))
                {
                    email.LinkedBillIds.Add(linkedBillId);
                }
            }

            email.DecisionLog ??= new BillInboxDecisionLog();
            email.DecisionLog.Steps.Add(new DecisionLogStep
            {
                StepNumber = (email.DecisionLog.Steps.Count + 1).ToString(),
                StepName = "Human Review — Statement Confirmation",
                Status = DecisionStepStatus.Success,
                Outcome = "Confirmed as Statement of Account by reviewer.",
                Details = [
                    $"Confirmed by user: {userId}",
                    $"Confirmed at: {DateTime.UtcNow:u}",
                    "Action: Verified that document is a statement of account; no liability bill created."
                ]
            });
            email.DecisionLog.FinalStatus = CompanyBillInboxConstants.BillingStatus.StatementConfirmed;

            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Explicitly confirms that an email is a payment receipt, recording the human review decision.
    /// </summary>
    public void ConfirmReceipt(string emailMessageId, string userId, string? linkedBillId = null)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(linkedBillId))
            {
                email.LinkedBillId = linkedBillId;
                if (!email.LinkedBillIds.Contains(linkedBillId))
                {
                    email.LinkedBillIds.Add(linkedBillId);
                }
            }

            email.DecisionLog ??= new BillInboxDecisionLog();
            email.DecisionLog.Steps.Add(new DecisionLogStep
            {
                StepNumber = (email.DecisionLog.Steps.Count + 1).ToString(),
                StepName = "Human Review — Receipt Confirmation",
                Status = DecisionStepStatus.Success,
                Outcome = "Confirmed as Payment Receipt by reviewer.",
                Details = [
                    $"Confirmed by user: {userId}",
                    $"Confirmed at: {DateTime.UtcNow:u}",
                    "Action: Verified as payment receipt/confirmation."
                ]
            });
            email.DecisionLog.FinalStatus = CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed;

            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Sends an outgoing email from the company billing address via SMTP.
    /// </summary>
    public async Task SendEmailAsync(string to, string subject, string bodyHtml, List<string>? attachmentPaths = null, CancellationToken cancellationToken = default)
    {
        if (_emailDeliveryService != null)
        {
            await _emailDeliveryService.SendEmailAsync(new SendEmailRequest
            {
                EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
                To = to,
                Subject = subject,
                Body = bodyHtml,
                FileAttachmentPaths = attachmentPaths?.Where(File.Exists).Select(p => (p, Path.GetFileName(p))).ToList()
            }, cancellationToken);
            return;
        }

        var settings = GetSettings();
        var emailSettings = _db.CompanyProfile.GetEmailSettings();
        var host = !string.IsNullOrWhiteSpace(settings.SmtpHost) ? settings.SmtpHost : emailSettings.SmtpHost;
        var port = settings.SmtpPort > 0 ? settings.SmtpPort : emailSettings.SmtpPort;
        var useSsl = settings.SmtpSsl;
        var user = !string.IsNullOrWhiteSpace(settings.SmtpUsername) ? settings.SmtpUsername : settings.EmailAddress;

        string password;
        if (!string.IsNullOrEmpty(settings.EncryptedSmtpPassword))
        {
            password = _encryptionService.Decrypt(settings.EncryptedSmtpPassword);
        }
        else
        {
            password = _encryptionService.Decrypt(settings.EncryptedImapPassword);
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.DisplayName, settings.EmailAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = bodyHtml };
        if (attachmentPaths != null)
        {
            foreach (var path in attachmentPaths)
            {
                if (File.Exists(path))
                {
                    builder.Attachments.Add(path);
                }
            }
        }
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var options = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(host, port, options, cancellationToken);
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password))
        {
            await client.AuthenticateAsync(user, password, cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        // Record in sent items
        var sentEmail = new EmailMessage
        {
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FolderPath = "Sent",
            Subject = subject,
            FromAddress = settings.EmailAddress,
            FromName = settings.DisplayName,
            ToAddresses = [to],
            Date = DateTimeOffset.UtcNow,
            Snippet = subject,
            IsRead = true,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved
        };
        _db.EmailMessages.Save(sentEmail);
    }

    /// <summary>
    /// Evaluates a batch of extracted invoice attachments against existing bills in Spokes using a two-pass strategy:
    /// Pass 1 matches high-confidence signals (Invoice #, PO, or within-window Date + Amount).
    /// Pass 2 matches remaining attachments on Same Supplier + Same Total Amount (possible duplicate).
    /// Each matched existing bill ID is excluded from subsequent matches in the same batch.
    /// </summary>
    private List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName, Supplier? Supplier, MatchingPoResult PoMatch, ExistingBillMatch? ExistingMatch)> EvaluateExtractedInvoicesBatch(
        EmailMessage email,
        List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)> extractedInvoices,
        Supplier? primarySupplier,
        CompanyBillInboxSettings settings)
    {
        var items = new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName, Supplier? Supplier, MatchingPoResult PoMatch, ExistingBillMatch? ExistingMatch)>(extractedInvoices.Count);
        foreach (var (extraction, path, fileName) in extractedInvoices)
        {
            var itemSupplier = primarySupplier ?? FindSupplier(email.FromAddress, email.FromName, extraction.SupplierName, extraction.SupplierEmail, extraction.SupplierTaxId);
            var poMatch = _poMatchingEngine.FindMatch(extraction, itemSupplier?.Id);
            items.Add((extraction, path, fileName, itemSupplier, poMatch, null));
        }

        var matchedExistingBillIds = new HashSet<string>();

        // Pass 1: High-confidence matches (Steps 1-4: Invoice #, PO, within-window Date + Amount, Subject Invoice #)
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var match = FindExistingBillMatch(
                item.Extraction,
                item.Supplier?.Id,
                item.Supplier?.Name ?? item.Extraction.SupplierName,
                email.Subject,
                email.Snippet,
                item.PoMatch.MatchedPo?.Id,
                email.Id,
                settings.DuplicateDateWindowDays,
                excludeBillIds: matchedExistingBillIds,
                includePossibleAmountFallback: false);

            if (match != null)
            {
                items[i] = (item.Extraction, item.SavedPath, item.FileName, item.Supplier, item.PoMatch, match);
                matchedExistingBillIds.Add(match.Bill.Id);
            }
        }

        // Pass 2: Possible duplicate fallback (Step 5: Same Supplier + Same Total Amount regardless of date window)
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ExistingMatch != null) continue;

            var match = FindExistingBillMatch(
                item.Extraction,
                item.Supplier?.Id,
                item.Supplier?.Name ?? item.Extraction.SupplierName,
                email.Subject,
                email.Snippet,
                item.PoMatch.MatchedPo?.Id,
                email.Id,
                settings.DuplicateDateWindowDays,
                excludeBillIds: matchedExistingBillIds,
                includePossibleAmountFallback: true);

            if (match != null)
            {
                items[i] = (item.Extraction, item.SavedPath, item.FileName, item.Supplier, item.PoMatch, match);
                matchedExistingBillIds.Add(match.Bill.Id);
            }
        }

        return items;
    }

    /// <summary>
    /// Returns any other active bills in Spokes from the same supplier with the same total amount (±$0.05).
    /// Used to display a possible-duplicate warning on draft bill cards during user review.
    /// </summary>
    public List<Bill> GetSameSupplierSameAmountBills(Bill draftBill)
    {
        if (draftBill == null || draftBill.Amount == 0) return [];

        return _db.Bills.GetAll()
            .Where(b => b.Id != draftBill.Id &&
                        b.Status != BillStatus.Void &&
                        Math.Abs(b.Amount - draftBill.Amount) <= 0.05m &&
                        ((!string.IsNullOrEmpty(draftBill.SupplierId) && !string.IsNullOrEmpty(b.SupplierId) && b.SupplierId == draftBill.SupplierId) ||
                         SupplierNamesMatch(b.SupplierName, draftBill.SupplierName)))
            .OrderBy(b => b.AutoIngested ? 1 : 0)
            .ThenByDescending(b => b.Date)
            .ToList();
    }

    /// <summary>
    /// Checks whether an incoming bill has already been entered into Spokes, returning structured match metadata.
    /// </summary>
    public ExistingBillMatch? FindExistingBillMatch(
        ExtractedInvoiceData? extraction,
        string? supplierId,
        string? supplierName,
        string? emailSubject = null,
        string? emailSnippet = null,
        string? matchedPoId = null,
        string? currentEmailId = null,
        int duplicateWindowDays = 20,
        decimal? textDetectedAmount = null,
        IEnumerable<string>? excludeBillIds = null,
        bool includePossibleAmountFallback = true)
    {
        var excludedSet = excludeBillIds != null ? new HashSet<string>(excludeBillIds) : null;
        var activeBills = _db.Bills.GetAll()
            .Where(b => b.Status != BillStatus.Void &&
                        (string.IsNullOrEmpty(currentEmailId) || b.SourceEmailId != currentEmailId) &&
                        (excludedSet == null || !excludedSet.Contains(b.Id)))
            .OrderBy(b => b.AutoIngested ? 1 : 0)
            .ToList();

        if (activeBills.Count == 0) return null;

        var docAmount = extraction?.TotalAmount ?? (textDetectedAmount ?? 0m);

        // 1. Direct InvoiceNumber match from extraction
        if (extraction != null && !string.IsNullOrWhiteSpace(extraction.InvoiceNumber))
        {
            var cleanExtNum = extraction.InvoiceNumber.Trim();
            foreach (var b in activeBills)
            {
                bool numberMatches = InvoiceNumbersMatch(b.InvoiceNumber, cleanExtNum);
                bool descriptionMatches = false;

                if (!numberMatches && !string.IsNullOrEmpty(b.Description))
                {
                    descriptionMatches = InvoiceNumberMatchesDescription(b.Description, cleanExtNum);
                    numberMatches = descriptionMatches;
                }

                if (numberMatches)
                {
                    bool supplierMatches = (!string.IsNullOrEmpty(supplierId) && b.SupplierId == supplierId) ||
                                           SupplierNamesMatch(b.SupplierName, supplierName);
                    bool amountMatches = (docAmount != 0 && Math.Abs(b.Amount - docAmount) <= 0.05m) ||
                                         (extraction.SubTotal != 0 && Math.Abs(b.Amount - extraction.SubTotal) <= 0.05m);
                    bool poMatches = !string.IsNullOrEmpty(matchedPoId) && b.PurchaseOrderId == matchedPoId;

                    // If both sides have known suppliers and they clearly conflict, do not match across distinct suppliers
                    bool suppliersConflict = !string.IsNullOrWhiteSpace(b.SupplierName) &&
                                              !string.IsNullOrWhiteSpace(supplierName) &&
                                              !SupplierNamesMatch(b.SupplierName, supplierName);

                    if (!suppliersConflict && (supplierMatches || amountMatches || poMatches))
                    {
                        var method = descriptionMatches
                            ? ExistingBillMatchMethod.DescriptionInvoiceNumber
                            : (string.Equals(b.InvoiceNumber, cleanExtNum, StringComparison.OrdinalIgnoreCase)
                                ? ExistingBillMatchMethod.ExactInvoiceNumber
                                : ExistingBillMatchMethod.NormalizedInvoiceNumber);

                        var reason = supplierMatches
                            ? $"Invoice number '{cleanExtNum}' matched existing bill #{b.InvoiceNumber ?? b.Description} for supplier '{b.SupplierName}'"
                            : (amountMatches
                                ? $"Invoice number '{cleanExtNum}' and amount {b.Amount:C2} matched existing bill #{b.InvoiceNumber ?? b.Description}"
                                : $"Invoice number '{cleanExtNum}' and PO #{matchedPoId} matched existing bill #{b.InvoiceNumber}");

                        return new ExistingBillMatch
                        {
                            Bill = b,
                            MatchReason = reason,
                            Method = method,
                            AmountDifference = b.Amount - docAmount
                        };
                    }

                    // If invoice number is distinct (>= 5 clean chars) and suppliers don't conflict, match even if supplier is unverified
                    if (!suppliersConflict && CleanInvoiceNumber(cleanExtNum).Length >= 5)
                    {
                        return new ExistingBillMatch
                        {
                            Bill = b,
                            MatchReason = $"Distinct invoice number '{cleanExtNum}' matched existing bill #{b.InvoiceNumber}",
                            Method = ExistingBillMatchMethod.NormalizedInvoiceNumber,
                            AmountDifference = b.Amount - docAmount
                        };
                    }
                }
            }
        }

        // 2. Same PO + Same Amount
        if (!string.IsNullOrEmpty(matchedPoId) && docAmount != 0)
        {
            var matchPo = activeBills.FirstOrDefault(b =>
                b.PurchaseOrderId == matchedPoId &&
                Math.Abs(b.Amount - docAmount) <= 0.05m);

            if (matchPo != null)
            {
                return new ExistingBillMatch
                {
                    Bill = matchPo,
                    MatchReason = $"Purchase Order match: PO ID '{matchedPoId}' and amount {matchPo.Amount:C2} matched existing bill #{matchPo.InvoiceNumber}",
                    Method = ExistingBillMatchMethod.PurchaseOrderAndAmount,
                    AmountDifference = matchPo.Amount - docAmount
                };
            }
        }

        // 3. Supplier + Exact Amount + Document Date Proximity (Strictly uses document InvoiceDate, never email date)
        if (extraction?.InvoiceDate.HasValue == true && docAmount != 0)
        {
            var refDate = extraction.InvoiceDate.Value;
            var window = duplicateWindowDays > 0 ? duplicateWindowDays : 20;

            foreach (var b in activeBills)
            {
                bool supplierMatches = (!string.IsNullOrEmpty(supplierId) && b.SupplierId == supplierId) ||
                                       SupplierNamesMatch(b.SupplierName, supplierName);

                if (supplierMatches)
                {
                    // If both have invoice numbers and they differ, do not treat as high-confidence date-window match
                    bool invoiceNumbersConflict = !string.IsNullOrWhiteSpace(b.InvoiceNumber) &&
                                                  !string.IsNullOrWhiteSpace(extraction.InvoiceNumber) &&
                                                  !InvoiceNumbersMatch(b.InvoiceNumber, extraction.InvoiceNumber) &&
                                                  !InvoiceNumberMatchesDescription(b.Description, extraction.InvoiceNumber);

                    if (!invoiceNumbersConflict)
                    {
                        bool amountMatches = Math.Abs(b.Amount - docAmount) <= 0.05m;
                        if (!amountMatches && extraction.SubTotal != 0 && Math.Abs(b.Amount - extraction.SubTotal) <= 0.05m)
                        {
                            amountMatches = true;
                        }

                        bool dateMatches = Math.Abs((b.Date - refDate).TotalDays) <= window;

                        if (amountMatches && dateMatches)
                        {
                            var daysDiff = Math.Round(Math.Abs((b.Date - refDate).TotalDays));
                            return new ExistingBillMatch
                            {
                                Bill = b,
                                MatchReason = $"Supplier '{b.SupplierName}' and amount {b.Amount:C2} match within document date window ({daysDiff} day(s) apart)",
                                Method = ExistingBillMatchMethod.SupplierAndExactAmount,
                                AmountDifference = b.Amount - docAmount
                            };
                        }
                    }
                }
            }
        }

        // 4. Fallback: Subject or snippet contains an existing bill's invoice number
        var emailText = $"{emailSubject} {emailSnippet}";
        if (!string.IsNullOrWhiteSpace(emailText))
        {
            foreach (var b in activeBills)
            {
                if (!string.IsNullOrWhiteSpace(b.InvoiceNumber) && b.InvoiceNumber.Trim().Length >= 4)
                {
                    var cleanBillInv = b.InvoiceNumber.Trim();
                    if (emailText.Contains(cleanBillInv, StringComparison.OrdinalIgnoreCase))
                    {
                        bool supplierMatches = (!string.IsNullOrEmpty(supplierId) && b.SupplierId == supplierId) ||
                                               SupplierNamesMatch(b.SupplierName, supplierName) ||
                                               (!string.IsNullOrEmpty(b.SupplierName) && emailText.Contains(b.SupplierName, StringComparison.OrdinalIgnoreCase));

                        if (supplierMatches)
                        {
                            return new ExistingBillMatch
                            {
                                Bill = b,
                                MatchReason = $"Email subject/body references existing bill invoice number '{cleanBillInv}' for '{b.SupplierName}'",
                                Method = ExistingBillMatchMethod.SubjectInvoiceNumber,
                                AmountDifference = docAmount > 0 ? b.Amount - docAmount : 0
                            };
                        }
                    }
                }
            }
        }

        // 5. Universal Possible Duplicate Fallback: Same Supplier + Same Total Amount (regardless of date window)
        if (includePossibleAmountFallback && docAmount != 0)
        {
            var refDate = extraction?.InvoiceDate;
            ExistingBillMatch? bestCandidate = null;
            int bestRank = int.MaxValue;
            double bestDaysDiff = double.MaxValue;

            foreach (var b in activeBills)
            {
                bool supplierMatches = (!string.IsNullOrEmpty(supplierId) && !string.IsNullOrEmpty(b.SupplierId) && b.SupplierId == supplierId) ||
                                       SupplierNamesMatch(b.SupplierName, supplierName);

                if (!supplierMatches) continue;

                bool amountMatches = Math.Abs(b.Amount - docAmount) <= 0.05m ||
                                     (extraction is { SubTotal: not 0 } && Math.Abs(b.Amount - extraction.SubTotal) <= 0.05m);

                if (!amountMatches) continue;

                bool invoiceNumbersConflict = extraction != null &&
                                              !string.IsNullOrWhiteSpace(b.InvoiceNumber) &&
                                              !string.IsNullOrWhiteSpace(extraction.InvoiceNumber) &&
                                              !InvoiceNumbersMatch(b.InvoiceNumber, extraction.InvoiceNumber) &&
                                              !InvoiceNumberMatchesDescription(b.Description, extraction.InvoiceNumber);

                int rank = (invoiceNumbersConflict ? 2 : 0) + (b.AutoIngested ? 1 : 0);
                double daysDiff = refDate.HasValue ? Math.Abs((b.Date - refDate.Value).TotalDays) : 0;

                if (rank < bestRank || (rank == bestRank && daysDiff < bestDaysDiff))
                {
                    bestRank = rank;
                    bestDaysDiff = daysDiff;

                    var method = extraction == null
                        ? ExistingBillMatchMethod.SupplierAndTextAmount
                        : ExistingBillMatchMethod.SupplierAndAmountPossibleDuplicate;

                    var billRef = !string.IsNullOrWhiteSpace(b.InvoiceNumber)
                        ? $"#{b.InvoiceNumber}"
                        : (!string.IsNullOrWhiteSpace(b.Description) ? $"'{b.Description}'" : $"ID {b.Id[..Math.Min(8, b.Id.Length)]}");
                    var dateInfo = $"dated {b.Date:d}";
                    var reason = extraction == null
                        ? $"Possible duplicate: Supplier '{b.SupplierName}' and text-detected amount {docAmount:C2} match existing bill {billRef} ({dateInfo})"
                        : $"Possible duplicate: Supplier '{b.SupplierName}' already has a bill for {b.Amount:C2} ({billRef}, {dateInfo}, Status: {b.Status})";

                    bestCandidate = new ExistingBillMatch
                    {
                        Bill = b,
                        MatchReason = reason,
                        Method = method,
                        AmountDifference = b.Amount - docAmount
                    };
                }
            }

            if (bestCandidate != null)
            {
                return bestCandidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether an incoming bill has already been entered into Spokes (e.g. manually entered from postal mail).
    /// </summary>
    public Bill? FindExistingBill(
        ExtractedInvoiceData? extraction,
        string? supplierId,
        string? supplierName,
        string? emailSubject = null,
        string? emailSnippet = null,
        string? matchedPoId = null,
        string? currentEmailId = null,
        int duplicateWindowDays = 20)
    {
        return FindExistingBillMatch(
            extraction,
            supplierId,
            supplierName,
            emailSubject,
            emailSnippet,
            matchedPoId,
            currentEmailId,
            duplicateWindowDays)?.Bill;
    }

    /// <summary>
    /// Links the incoming email's PDF attachment to an existing bill (e.g. entered manually) and marks the email as dealt with.
    /// </summary>
    public void AttachAttachmentToExistingBill(
        string emailMessageId,
        string billId,
        string userId,
        string? attachmentPath = null,
        string? attachmentName = null)
    {
        var bill = _db.Bills.GetById(billId);
        var email = _db.EmailMessages.GetById(emailMessageId);

        if (bill != null && email != null)
        {
            var matchSummary = email.DecisionLog?.DuplicateMatches.FirstOrDefault(m => m.ExistingBillId == billId);
            var firstAtt = email.Attachments.FirstOrDefault();
            var fallbackPath = firstAtt != null ? $"/spokesapi/files/emails/{email.Id}/{firstAtt.FileName}" : null;
            var fallbackName = firstAtt?.FileName;

            var resolvedPath = !string.IsNullOrEmpty(attachmentPath)
                ? attachmentPath
                : (!string.IsNullOrEmpty(matchSummary?.AttachmentPath)
                    ? matchSummary.AttachmentPath
                    : (!string.IsNullOrEmpty(email.MatchedAttachmentPath) ? email.MatchedAttachmentPath : fallbackPath));
            var resolvedName = !string.IsNullOrEmpty(attachmentName)
                ? attachmentName
                : (!string.IsNullOrEmpty(matchSummary?.AttachmentFileName)
                    ? matchSummary.AttachmentFileName
                    : (!string.IsNullOrEmpty(email.MatchedAttachmentName) ? email.MatchedAttachmentName : fallbackName));

            if (!string.IsNullOrEmpty(resolvedPath))
            {
                bill.AttachmentPath = resolvedPath;
                bill.AttachmentName = !string.IsNullOrEmpty(resolvedName)
                    ? resolvedName
                    : "Invoice.pdf";
                _db.Bills.Save(bill);
            }

            var hasRemainingDrafts = email.LinkedBillIds
                .Select(id => _db.Bills.GetById(id))
                .Any(b => b != null && b.SourceEmailId == email.Id && b.Status == BillStatus.AwaitingApproval);

            if (!hasRemainingDrafts)
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
            }
            email.LinkedBillId = bill.Id;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Links the incoming email's PDF attachment to an existing recurring bill record and marks the email as dealt with.
    /// </summary>
    public void AttachAttachmentToExistingRecurringBill(
        string emailMessageId,
        string recurringExpenseId,
        string recordId,
        string userId,
        string? attachmentPath = null,
        string? attachmentName = null)
    {
        var expense = _db.RecurringExpenses.GetById(recurringExpenseId);
        var email = _db.EmailMessages.GetById(emailMessageId);

        if (expense != null && email != null)
        {
            var record = expense.History.FirstOrDefault(r => r.Id == recordId);
            if (record != null)
            {
                var firstAtt = email.Attachments.FirstOrDefault();
                var fallbackPath = firstAtt != null ? $"/spokesapi/files/emails/{email.Id}/{firstAtt.FileName}" : null;
                var fallbackName = firstAtt?.FileName;

                var matchSummary = email.DecisionLog?.DuplicateMatches.FirstOrDefault(m => m.ExistingBillId == recordId);
                var resolvedPath = !string.IsNullOrEmpty(attachmentPath)
                    ? attachmentPath
                    : (!string.IsNullOrEmpty(matchSummary?.AttachmentPath)
                        ? matchSummary.AttachmentPath
                        : (!string.IsNullOrEmpty(email.MatchedAttachmentPath) ? email.MatchedAttachmentPath : fallbackPath));

                var resolvedName = !string.IsNullOrEmpty(attachmentName)
                    ? attachmentName
                    : (!string.IsNullOrEmpty(matchSummary?.AttachmentFileName)
                        ? matchSummary.AttachmentFileName
                        : (!string.IsNullOrEmpty(email.MatchedAttachmentName) ? email.MatchedAttachmentName : fallbackName));

                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    record.AttachmentPath = resolvedPath;
                    record.AttachmentName = !string.IsNullOrEmpty(resolvedName) ? resolvedName : "Invoice.pdf";
                    _db.RecurringExpenses.Save(expense);
                }

                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
                email.LinkedRecurringExpenseId = expense.Id;
                email.LinkedRecurringRecordId = record.Id;
                email.ProcessedByUserId = userId;
                email.ProcessedAt = DateTime.UtcNow;
                _db.EmailMessages.Save(email);
            }
        }
    }

    /// <summary>
    /// Links an incoming email to a recurring expense profile and specific record, marking the email as dealt with.
    /// </summary>
    public void LinkEmailToRecurringExpense(
        string emailMessageId,
        string recurringExpenseId,
        string recordId,
        string userId)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email != null)
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
            email.LinkedRecurringExpenseId = recurringExpenseId;
            email.LinkedRecurringRecordId = recordId;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Overrides duplicate detection and creates a new draft bill anyway.
    /// When replaceDuplicateBillId or specificAttachmentPath is provided, drafts that specific attachment
    /// and replaces the duplicate bill link on the email.
    /// </summary>
    public async Task<Bill?> CreateDraftAnywayAsync(
        string emailMessageId,
        string userId,
        string? replaceDuplicateBillId = null,
        string? specificAttachmentPath = null,
        string? specificAttachmentName = null)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email == null) return null;

        var settings = GetSettings();
        var extractionContext = BuildExtractionContext(settings);
        var supplier = FindSupplier(email.FromAddress, email.FromName, null);
        ExtractedInvoiceData? extraction = null;

        var matchSummary = !string.IsNullOrEmpty(replaceDuplicateBillId)
            ? email.DecisionLog?.DuplicateMatches.FirstOrDefault(m => m.ExistingBillId == replaceDuplicateBillId)
            : null;

        var targetAttachmentPath = !string.IsNullOrEmpty(specificAttachmentPath)
            ? specificAttachmentPath
            : (!string.IsNullOrEmpty(matchSummary?.AttachmentPath) ? matchSummary.AttachmentPath : email.MatchedAttachmentPath);

        var targetAttachmentName = !string.IsNullOrEmpty(specificAttachmentName)
            ? specificAttachmentName
            : (!string.IsNullOrEmpty(matchSummary?.AttachmentFileName) ? matchSummary.AttachmentFileName : email.MatchedAttachmentName);

        if (string.IsNullOrEmpty(targetAttachmentPath) && email.Attachments.Count > 0)
        {
            var ensured = await EnsureEmailAttachmentInBillsStorageAsync(email, null);
            targetAttachmentPath = ensured.AttachmentPath;
            targetAttachmentName = ensured.AttachmentName;
        }

        if (!string.IsNullOrEmpty(targetAttachmentPath))
        {
            try
            {
                var parts = targetAttachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    var category = parts[2];
                    var contextId = parts[3];
                    var fileName = parts[4];
                    using var stream = _fileService.OpenRead(category, contextId, fileName);
                    if (stream != null)
                    {
                        using var mem = new MemoryStream();
                        await stream.CopyToAsync(mem);
                        mem.Position = 0;
                        extraction = _pdfInspectionService.Inspect(mem, targetAttachmentName ?? fileName, extractionContext);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to re-inspect attachment for email {EmailId}", emailMessageId);
            }
        }

        if (extraction == null && matchSummary != null && matchSummary.ExtractedAmount.HasValue)
        {
            extraction = new ExtractedInvoiceData
            {
                InvoiceNumber = matchSummary.ExtractedInvoiceNumber,
                TotalAmount = matchSummary.ExtractedAmount.Value,
                Currency = matchSummary.ExtractedCurrency ?? settings.DefaultCurrency,
                InvoiceDate = matchSummary.ExtractedInvoiceDate
            };
        }

        if (supplier == null && extraction != null)
        {
            supplier = FindSupplier(email.FromAddress, email.FromName, extraction.SupplierName, extraction.SupplierEmail, extraction.SupplierTaxId);
        }

        var poMatch = extraction != null ? _poMatchingEngine.FindMatch(extraction, supplier?.Id) : MatchingPoResult.None;

        var draftBill = BuildDraftBillFromExtraction(
            email,
            extraction,
            supplier,
            poMatch,
            targetAttachmentPath ?? string.Empty,
            targetAttachmentName ?? "Invoice.pdf",
            settings,
            autoIngested: true);

        _db.Bills.Save(draftBill);

        if (!string.IsNullOrEmpty(replaceDuplicateBillId))
        {
            email.LinkedBillIds.Remove(replaceDuplicateBillId);
            email.DecisionLog?.DuplicateMatches.RemoveAll(m => m.ExistingBillId == replaceDuplicateBillId);
        }

        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
        email.LinkedBillId = draftBill.Id;
        if (!email.LinkedBillIds.Contains(draftBill.Id))
        {
            email.LinkedBillIds.Add(draftBill.Id);
        }
        email.LinkedPoId = poMatch.MatchedPo?.Id ?? email.LinkedPoId;
        _db.EmailMessages.Save(email);

        return draftBill;
    }

    /// <summary>
    /// Ensures that an email attachment is stored in Spokes storage under category "bills" with a valid physical file.
    /// If email.MatchedAttachmentPath is already valid and exists on disk, returns it.
    /// Otherwise, recovers the attachment from email attachment cache or raw MIME, uploads it to category "bills",
    /// updates email.MatchedAttachmentPath and saves the email.
    /// </summary>
    public async Task<(string? AttachmentPath, string? AttachmentName)> EnsureEmailAttachmentInBillsStorageAsync(
        EmailMessage email,
        string? specificAttachmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (email == null) return (null, null);

        // 1. Check if MatchedAttachmentPath is already valid and exists on disk
        if (string.IsNullOrEmpty(specificAttachmentId) && !string.IsNullOrEmpty(email.MatchedAttachmentPath))
        {
            var parts = email.MatchedAttachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5)
            {
                var category = parts[2];
                var contextId = parts[3];
                var fileName = parts[4];
                if (_fileService.FileExists(category, contextId, fileName) || email.Attachments.Count == 0)
                {
                    return (email.MatchedAttachmentPath, email.MatchedAttachmentName ?? fileName);
                }
            }
            else
            {
                return (email.MatchedAttachmentPath, email.MatchedAttachmentName ?? Path.GetFileName(email.MatchedAttachmentPath));
            }
        }

        // 2. Select target attachment meta
        EmailAttachmentMeta? targetAtt = null;
        if (!string.IsNullOrEmpty(specificAttachmentId))
        {
            targetAtt = email.Attachments.FirstOrDefault(a => a.Id == specificAttachmentId);
        }
        targetAtt ??= email.Attachments.FirstOrDefault(a =>
        {
            var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
            return ext is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".xml";
        }) ?? email.Attachments.FirstOrDefault();

        if (targetAtt == null)
        {
            return (!string.IsNullOrEmpty(email.MatchedAttachmentPath) ? email.MatchedAttachmentPath : null,
                    !string.IsNullOrEmpty(email.MatchedAttachmentName) ? email.MatchedAttachmentName : null);
        }

        // 3. Try reading attachment from local email attachments folder
        byte[]? fileBytes = null;

        var cachedAttachmentPath = _cacheMaintenance != null
            ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Attachments", email.Id, targetAtt.Id)
            : null;

        if (!string.IsNullOrEmpty(cachedAttachmentPath) && File.Exists(cachedAttachmentPath))
        {
            try
            {
                fileBytes = await File.ReadAllBytesAsync(cachedAttachmentPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read cached attachment file at {Path}", cachedAttachmentPath);
            }
        }

        // 4. If not found in cache, try extracting from MIME message
        if (fileBytes == null)
        {
            var mimePath = _cacheMaintenance != null
                ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime", $"{email.Id}.eml")
                : null;

            if (!string.IsNullOrEmpty(mimePath) && File.Exists(mimePath))
            {
                try
                {
                    using var fs = File.OpenRead(mimePath);
                    var mime = await MimeMessage.LoadAsync(fs, cancellationToken);
                    var part = mime.Attachments.OfType<MimePart>()
                        .FirstOrDefault(p => (!string.IsNullOrEmpty(targetAtt.FileName) && p.FileName == targetAtt.FileName) || p.ContentId == targetAtt.Id)
                        ?? mime.Attachments.OfType<MimePart>().FirstOrDefault();

                    if (part?.Content != null)
                    {
                        using var mem = new MemoryStream();
                        await part.Content.DecodeToAsync(mem, cancellationToken);
                        fileBytes = mem.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to extract attachment from MIME {MimePath}", mimePath);
                }
            }
        }

        // 5. If we obtained bytes, upload to Spokes storage under "bills"
        if (fileBytes != null && fileBytes.Length > 0)
        {
            try
            {
                using var uploadStream = new MemoryStream(fileBytes);
                var uploadContextId = Guid.NewGuid().ToString();
                var savedPath = await _fileService.UploadStreamAsync("bills", uploadContextId, uploadStream, targetAtt.FileName);

                email.MatchedAttachmentPath = savedPath;
                email.MatchedAttachmentName = targetAtt.FileName;
                _db.EmailMessages.Save(email);

                return (savedPath, targetAtt.FileName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to upload recovered attachment to bills storage for email {EmailId}", email.Id);
            }
        }

        return (!string.IsNullOrEmpty(email.MatchedAttachmentPath) ? email.MatchedAttachmentPath : null,
                !string.IsNullOrEmpty(email.MatchedAttachmentName) ? email.MatchedAttachmentName : targetAtt.FileName);
    }

    /// <summary>
    /// Prepares an in-memory draft Bill from an email message with pre-populated fields (supplier, PO, amounts,
    /// invoice number, line items, and attachment) without saving it to the database yet.
    /// Used by interactive UI flows (such as "Create Bill from Email") so the user can inspect and edit the bill
    /// before explicitly saving.
    /// </summary>
    public async Task<Bill?> PrepareBillFromEmailAsync(string emailMessageId, CancellationToken cancellationToken = default)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email == null) return null;

        var settings = GetSettings();
        var extractionContext = BuildExtractionContext(settings);

        // Ensure attachment is in bills storage
        var (attachmentPath, attachmentName) = await EnsureEmailAttachmentInBillsStorageAsync(email, null, cancellationToken);

        ExtractedInvoiceData? extraction = null;
        if (!string.IsNullOrEmpty(attachmentPath))
        {
            try
            {
                var parts = attachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    var category = parts[2];
                    var contextId = parts[3];
                    var fileName = parts[4];
                    using var stream = _fileService.OpenRead(category, contextId, fileName);
                    if (stream != null)
                    {
                        using var mem = new MemoryStream();
                        await stream.CopyToAsync(mem, cancellationToken);
                        mem.Position = 0;
                        extraction = _pdfInspectionService.Inspect(mem, attachmentName ?? fileName, extractionContext);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to inspect attachment when preparing bill from email {EmailId}", emailMessageId);
            }
        }

        // If extraction is null, fall back to detected values on the email if available
        if (extraction == null && email.DetectedAmount.HasValue && email.DetectedAmount.Value > 0)
        {
            extraction = new ExtractedInvoiceData
            {
                TotalAmount = email.DetectedAmount.Value,
                PaidAmount = email.DetectedPaidAmount ?? 0m,
                BalanceDue = email.DetectedBalanceDue ?? email.DetectedAmount.Value,
                Currency = !string.IsNullOrEmpty(email.DetectedCurrency) ? email.DetectedCurrency : settings.DefaultCurrency,
                InvoiceDate = email.Date.DateTime
            };
        }

        var supplier = FindSupplier(email.FromAddress, email.FromName, extraction?.SupplierName, extraction?.SupplierEmail, extraction?.SupplierTaxId);
        var poMatch = extraction != null ? _poMatchingEngine.FindMatch(extraction, supplier?.Id) : MatchingPoResult.None;

        var bill = BuildDraftBillFromExtraction(
            email,
            extraction,
            supplier,
            poMatch,
            attachmentPath ?? string.Empty,
            !string.IsNullOrEmpty(attachmentPath) ? (attachmentName ?? "Invoice.pdf") : string.Empty,
            settings,
            autoIngested: false);

        return bill;
    }

    private static bool InvoiceNumbersMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        a = a.Trim();
        b = b.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var cleanA = CleanInvoiceNumber(a);
        var cleanB = CleanInvoiceNumber(b);
        if (!string.IsNullOrEmpty(cleanA) && cleanA.Equals(cleanB, StringComparison.OrdinalIgnoreCase)) return true;

        // Normalized: common prefixes stripped, leading zeros on numeric portions stripped
        var normA = NormalizeInvoiceNumber(a);
        var normB = NormalizeInvoiceNumber(b);
        if (!string.IsNullOrEmpty(normA) && normA.Equals(normB, StringComparison.OrdinalIgnoreCase)) return true;

        if (cleanA.Length >= 4 && cleanB.Length >= 4)
        {
            if (cleanA.EndsWith(cleanB, StringComparison.OrdinalIgnoreCase) || cleanB.EndsWith(cleanA, StringComparison.OrdinalIgnoreCase) ||
                cleanA.StartsWith(cleanB, StringComparison.OrdinalIgnoreCase) || cleanB.StartsWith(cleanA, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (normA.Length >= 3 && normB.Length >= 3)
        {
            if (normA.EndsWith(normB, StringComparison.OrdinalIgnoreCase) || normB.EndsWith(normA, StringComparison.OrdinalIgnoreCase) ||
                normA.StartsWith(normB, StringComparison.OrdinalIgnoreCase) || normB.StartsWith(normA, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (cleanA.Length >= 5 && cleanB.Length >= 5)
        {
            if (cleanA.Contains(cleanB, StringComparison.OrdinalIgnoreCase) || cleanB.Contains(cleanA, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeInvoiceNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var trimmed = s.Trim();
        // Strip common prefixes
        var withoutPrefix = System.Text.RegularExpressions.Regex.Replace(
            trimmed,
            @"^(?:INV|INVOICE|FACT|FACTURE|BILL|NO|NUM|NUMERO|#)[\s\-:#\./]*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var parts = System.Text.RegularExpressions.Regex.Split(withoutPrefix, @"[^A-Za-z0-9]+");
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (p.All(char.IsDigit))
            {
                var unpadded = p.TrimStart('0');
                sb.Append(string.IsNullOrEmpty(unpadded) ? "0" : unpadded);
            }
            else
            {
                sb.Append(p.ToUpperInvariant());
            }
        }
        return sb.ToString();
    }

    private static bool InvoiceNumberMatchesDescription(string? description, string? invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(invoiceNumber)) return false;
        var rawInv = invoiceNumber.Trim();
        if (rawInv.Length >= 3 && description.Contains(rawInv, StringComparison.OrdinalIgnoreCase)) return true;

        var cleanInv = CleanInvoiceNumber(rawInv);
        if (cleanInv.Length >= 4 && description.Contains(cleanInv, StringComparison.OrdinalIgnoreCase)) return true;

        var normInv = NormalizeInvoiceNumber(rawInv);
        if (normInv.Length >= 3)
        {
            var normDesc = NormalizeInvoiceNumber(description);
            if (normDesc.Contains(normInv, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Tokenize description to check if a short ticket/invoice number token in the description
        // (e.g. "4262-9" or "39742" in "Facture 4262-9") matches a longer extracted invoice number (e.g. "42629130040726")
        var tokens = System.Text.RegularExpressions.Regex.Split(description, @"[\s,;()\[\]/\\|]+");
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim('-', '#', ':', '.');
            if (string.IsNullOrWhiteSpace(token)) continue;

            var cleanToken = CleanInvoiceNumber(token);
            var digitCount = cleanToken.Count(char.IsDigit);
            if (cleanToken.Length < 5 || digitCount < 4) continue;

            if (InvoiceNumbersMatch(token, rawInv))
            {
                return true;
            }
        }

        return false;
    }

    private static string CleanInvoiceNumber(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }

    private static bool SupplierNamesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        a = a.Trim();
        b = b.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var cleanA = CleanAlpha(a);
        var cleanB = CleanAlpha(b);
        if (!string.IsNullOrEmpty(cleanA) && cleanA.Equals(cleanB, StringComparison.OrdinalIgnoreCase)) return true;

        var normA = NormalizeSupplierName(a);
        var normB = NormalizeSupplierName(b);
        if (!string.IsNullOrEmpty(normA) && normA.Equals(normB, StringComparison.OrdinalIgnoreCase)) return true;

        if (cleanA.Length >= 4 && cleanB.Length >= 4)
        {
            if (cleanA.Contains(cleanB, StringComparison.OrdinalIgnoreCase) ||
                cleanB.Contains(cleanA, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (normA.Length >= 3 && normB.Length >= 3)
        {
            if (normA.Contains(normB, StringComparison.OrdinalIgnoreCase) ||
                normB.Contains(normA, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Acronym check (e.g. AWS vs Amazon Web Services)
        if (IsAcronymMatch(a, b) || IsAcronymMatch(b, a))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeSupplierName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b(inc|incorporated|ltd|limited|llc|corp|corporation|ltée|ltee|co|company|compagnie|gmbh|services|technologies|solutions)\b[\.]*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return CleanAlpha(stripped);
    }

    private static bool IsAcronymMatch(string shortName, string fullName)
    {
        var cleanShort = CleanAlpha(shortName).ToUpperInvariant();
        if (cleanShort.Length < 2 || cleanShort.Length > 6) return false;

        if (fullName.Contains($"({cleanShort})", StringComparison.OrdinalIgnoreCase)) return true;

        var words = fullName.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0]))
            .Select(w => char.ToUpperInvariant(w[0]))
            .ToArray();
        var acronym = new string(words);
        return acronym.Equals(cleanShort, StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanAlpha(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Moves an email on the IMAP server from its current folder to the processed folder (e.g. Processed_Bills) upon reviewer action.
    /// </summary>
    public async Task<bool> MoveEmailToProcessedFolderAsync(string emailMessageId, string userId, CancellationToken cancellationToken = default)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email == null) return false;

        var settings = GetSettings();
        var targetFolderName = string.IsNullOrWhiteSpace(settings.ProcessedFolderName) ? "Processed_Bills" : settings.ProcessedFolderName;

        try
        {
            var host = !string.IsNullOrWhiteSpace(settings.ImapHost) ? settings.ImapHost : _db.CompanyProfile.GetEmailSettings().ImapHost;
            var port = settings.ImapPort > 0 ? settings.ImapPort : _db.CompanyProfile.GetEmailSettings().ImapPort;
            var user = !string.IsNullOrWhiteSpace(settings.ImapUsername) ? settings.ImapUsername : settings.EmailAddress;
            var password = _encryptionService.Decrypt(settings.EncryptedImapPassword);

            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
            {
                using var client = new ImapClient();
                var options = settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
                await client.ConnectAsync(host, port, options, cancellationToken);
                await client.AuthenticateAsync(user, password, cancellationToken);

                var sourceFolder = await client.GetFolderAsync(string.IsNullOrWhiteSpace(email.FolderPath) ? "INBOX" : email.FolderPath, cancellationToken);
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

                var personalFolder = client.GetFolder(client.PersonalNamespaces[0]);
                IMailFolder? targetFolder = null;
                try
                {
                    targetFolder = await client.GetFolderAsync(targetFolderName, cancellationToken);
                }
                catch
                {
                    targetFolder = await personalFolder.CreateAsync(targetFolderName, true, cancellationToken);
                }

                if (email.UniqueId > 0 && targetFolder != null)
                {
                    await sourceFolder.MoveToAsync(new UniqueId(email.UniqueId), targetFolder, cancellationToken);
                }

                await client.DisconnectAsync(true, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to move email {UniqueId} to {Folder} on IMAP server", email.UniqueId, targetFolderName);
        }

        email.FolderPath = targetFolderName;
        email.ProcessedByUserId = userId;
        email.ProcessedAt = DateTime.UtcNow;
        _db.EmailMessages.Save(email);
        return true;
    }

    /// <summary>
    /// Moves an email on the IMAP server to the deleted folder (e.g. SPOKES_DELETED),
    /// cleans up any unapproved draft bills, and removes local message records and cache.
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteEmailAsync(
        string emailMessageId,
        string userId,
        bool deleteLinkedDrafts = true,
        CancellationToken cancellationToken = default)
    {
        var email = _db.EmailMessages.GetById(CompanyBillInboxConstants.SystemEmployeeId, emailMessageId)
                 ?? _db.EmailMessages.GetById(emailMessageId);
        if (email == null) return (false, "Email not found.");

        var settings = GetSettings();
        var targetFolderName = string.IsNullOrWhiteSpace(settings.DeletedFolderName) ? "SPOKES_DELETED" : settings.DeletedFolderName;

        try
        {
            var host = !string.IsNullOrWhiteSpace(settings.ImapHost) ? settings.ImapHost : _db.CompanyProfile.GetEmailSettings().ImapHost;
            var port = settings.ImapPort > 0 ? settings.ImapPort : _db.CompanyProfile.GetEmailSettings().ImapPort;
            var user = !string.IsNullOrWhiteSpace(settings.ImapUsername) ? settings.ImapUsername : settings.EmailAddress;
            var password = _encryptionService.Decrypt(settings.EncryptedImapPassword);

            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                using var client = new ImapClient();
                client.Timeout = 5000;
                var options = settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
                await client.ConnectAsync(host, port, options, timeoutCts.Token);
                await client.AuthenticateAsync(user, password, timeoutCts.Token);

                var sourceFolder = await client.GetFolderAsync(string.IsNullOrWhiteSpace(email.FolderPath) ? "INBOX" : email.FolderPath, timeoutCts.Token);
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite, timeoutCts.Token);

                var personalFolder = client.GetFolder(client.PersonalNamespaces[0]);
                IMailFolder? targetFolder = null;
                try
                {
                    targetFolder = await client.GetFolderAsync(targetFolderName, timeoutCts.Token);
                }
                catch
                {
                    targetFolder = await personalFolder.CreateAsync(targetFolderName, true, timeoutCts.Token);
                }

                if (email.UniqueId > 0 && targetFolder != null)
                {
                    await sourceFolder.MoveToAsync(new UniqueId(email.UniqueId), targetFolder, timeoutCts.Token);
                }

                await client.DisconnectAsync(true, timeoutCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to move email {UniqueId} to {Folder} on IMAP server during deletion", email.UniqueId, targetFolderName);
        }

        // Clean up linked unapproved draft bills
        if (deleteLinkedDrafts)
        {
            var billIds = new List<string>(email.LinkedBillIds ?? []);
            if (!string.IsNullOrEmpty(email.LinkedBillId) && !billIds.Contains(email.LinkedBillId))
            {
                billIds.Add(email.LinkedBillId);
            }

            foreach (var billId in billIds)
            {
                var bill = _db.Bills.GetById(billId);
                if (bill != null)
                {
                    if (bill.Status == BillStatus.AwaitingApproval)
                    {
                        _db.Bills.Delete(bill.Id);
                        _logger?.LogInformation("Deleted orphan draft bill {BillId} linked to deleted email {EmailId}", bill.Id, email.Id);
                    }
                    else if (bill.SourceEmailId == email.Id)
                    {
                        bill.SourceEmailId = null;
                        _db.Bills.Save(bill);
                    }
                }
            }
        }

        // Purge local cached message files and remove message from DB
        if (_cacheMaintenance != null)
        {
            await _cacheMaintenance.DeleteLocalMessageFilesAsync(CompanyBillInboxConstants.SystemEmployeeId, email.Id);
        }
        _db.EmailMessages.Delete(CompanyBillInboxConstants.SystemEmployeeId, email.Id);

        return (true, $"Email moved to '{targetFolderName}' and cleaned up successfully.");
    }

    /// <summary>
    /// Adds the sender's domain to the ignored list and deletes/moves the current email to SPOKES_DELETED.
    /// </summary>
    public async Task<(bool Success, string Message)> BlockSenderDomainAsync(
        string emailAddress,
        string? emailMessageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || !emailAddress.Contains('@'))
        {
            return (false, "Invalid sender email address.");
        }

        var domain = emailAddress.Split('@').Last().Trim().ToLowerInvariant();
        var settings = GetSettings();

        if (!settings.IgnoredSenderDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            settings.IgnoredSenderDomains.Add(domain);
            SaveSettings(settings);
        }

        if (!string.IsNullOrEmpty(emailMessageId))
        {
            await DeleteEmailAsync(emailMessageId, userId, deleteLinkedDrafts: true, cancellationToken);
        }

        return (true, $"Domain '@{domain}' added to ignored senders and email moved to '{settings.DeletedFolderName}'.");
    }

    /// <summary>
    /// Attaches an uploaded PDF/image to an email (e.g. portal invoice downloaded by accountant) and runs extraction.
    /// </summary>
    public async Task<Bill?> UploadAttachmentToEmailAsync(string emailMessageId, Stream stream, string fileName, string userId, CancellationToken cancellationToken = default)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email == null) return null;

        var billId = Guid.NewGuid().ToString();
        var savedPath = await _fileService.UploadStreamAsync("bills", billId, stream, fileName);

        email.Attachments.Add(new EmailAttachmentMeta
        {
            Id = Guid.NewGuid().ToString(),
            FileName = fileName,
            ContentType = Path.GetExtension(fileName).ToLowerInvariant() == ".pdf" ? "application/pdf" : "image/jpeg",
            Size = stream.Length
        });
        email.HasAttachments = true;
        email.MatchedAttachmentPath = savedPath;
        email.MatchedAttachmentName = fileName;

        stream.Position = 0;
        var extraction = _pdfInspectionService.Inspect(stream, fileName);
        var supplier = FindSupplier(email.FromAddress, email.FromName, extraction?.SupplierName);
        var poMatch = extraction != null ? _poMatchingEngine.FindMatch(extraction, supplier?.Id) : MatchingPoResult.None;
        var settings = GetSettings();

        Bill? draftBill = null;
        if (!string.IsNullOrEmpty(email.LinkedBillId))
        {
            draftBill = _db.Bills.GetById(email.LinkedBillId);
        }

        if (draftBill == null)
        {
            var existingMatch = FindExistingBillMatch(
                extraction,
                supplier?.Id,
                supplier?.Name ?? extraction?.SupplierName,
                email.Subject,
                email.Snippet,
                poMatch.MatchedPo?.Id,
                email.Id,
                settings.DuplicateDateWindowDays);

            if (existingMatch != null)
            {
                var existingBill = existingMatch.Bill;
                if (string.IsNullOrEmpty(existingBill.AttachmentPath))
                {
                    existingBill.AttachmentPath = savedPath;
                    existingBill.AttachmentName = fileName;
                    _db.Bills.Save(existingBill);
                }

                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                email.LinkedBillId = existingBill.Id;
                if (!email.LinkedBillIds.Contains(existingBill.Id)) email.LinkedBillIds.Add(existingBill.Id);
                if (extraction != null)
                {
                    email.DetectedAmount = extraction.TotalAmount;
                    email.DetectedCurrency = extraction.Currency;
                    email.DetectedPaidAmount = extraction.PaidAmount;
                    email.DetectedBalanceDue = extraction.BalanceDue;
                }
                email.ProcessedByUserId = userId;
                _db.EmailMessages.Save(email);

                return existingBill;
            }

            draftBill = new Bill
            {
                Id = billId,
                SupplierId = supplier?.Id ?? string.Empty,
                SupplierName = supplier?.Name ?? extraction?.SupplierName ?? email.FromName,
                Description = extraction != null && !string.IsNullOrWhiteSpace(extraction.InvoiceNumber)
                    ? $"Invoice {extraction.InvoiceNumber}"
                    : (poMatch.MatchedPo != null ? $"Bill for {poMatch.MatchedPo.PoNumber}" : email.Subject),
                InvoiceNumber = extraction?.InvoiceNumber,
                Date = extraction?.InvoiceDate ?? email.Date.DateTime,
                DueDate = extraction?.DueDate ?? (extraction?.InvoiceDate ?? email.Date.DateTime).AddDays(30),
                Amount = extraction?.TotalAmount ?? 0,
                SubTotal = extraction != null && extraction.SubTotal > 0 ? extraction.SubTotal : (extraction?.TotalAmount ?? 0) - (extraction?.TaxAmount ?? 0),
                TaxAmount = extraction?.TaxAmount ?? 0,
                TpsAmount = extraction?.TpsAmount ?? 0,
                TvqAmount = extraction?.TvqAmount ?? 0,
                Currency = extraction?.Currency ?? settings.DefaultCurrency,
                Status = BillStatus.AwaitingApproval,
                PurchaseOrderId = poMatch.MatchedPo?.Id,
                AutoIngested = false,
                SourceEmailId = email.Id,
                SourceEmailSubject = email.Subject,
                SourceSenderEmail = email.FromAddress,
                AutoMatchReason = poMatch.Reason
            };
        }
        else
        {
            if (extraction != null)
            {
                if (extraction.TotalAmount > 0) draftBill.Amount = extraction.TotalAmount;
                if (!string.IsNullOrEmpty(extraction.InvoiceNumber)) draftBill.InvoiceNumber = extraction.InvoiceNumber;
                if (extraction.InvoiceDate.HasValue) draftBill.Date = extraction.InvoiceDate.Value;
            }
        }

        draftBill.AttachmentPath = savedPath;
        draftBill.AttachmentName = fileName;

        if (extraction != null)
        {
            email.DetectedAmount = extraction.TotalAmount;
            email.DetectedCurrency = extraction.Currency;
            email.DetectedPaidAmount = extraction.PaidAmount;
            email.DetectedBalanceDue = extraction.BalanceDue;

            if (extraction.PaidAmount > 0 && !draftBill.Payments.Any(p => Math.Abs(p.Amount - extraction.PaidAmount) <= 0.05m))
            {
                draftBill.Payments.Add(new PaymentRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Date = extraction.InvoiceDate ?? email.Date.DateTime,
                    Amount = extraction.PaidAmount,
                    Method = "Unknown",
                    Reference = !string.IsNullOrWhiteSpace(draftBill.InvoiceNumber) ? $"Payment on #{draftBill.InvoiceNumber}" : "Prior payment",
                    Notes = "Prior payment detected from reminder/statement document"
                });
            }
        }

        _db.Bills.Save(draftBill);

        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
        email.LinkedBillId = draftBill.Id;
        if (!email.LinkedBillIds.Contains(draftBill.Id)) email.LinkedBillIds.Add(draftBill.Id);
        email.ProcessedByUserId = userId;
        _db.EmailMessages.Save(email);

        return draftBill;
    }

    /// <summary>
    /// Updates an existing bill's amount and attaches the incoming invoice PDF when a discrepancy is resolved.
    /// </summary>
    public void UpdateExistingBillAmount(string emailMessageId, string billId, decimal newAmount, string userId)
    {
        var bill = _db.Bills.GetById(billId);
        var email = _db.EmailMessages.GetById(emailMessageId);

        if (bill != null && email != null)
        {
            bill.Amount = newAmount;
            if (!string.IsNullOrEmpty(email.MatchedAttachmentPath))
            {
                bill.AttachmentPath = email.MatchedAttachmentPath;
                bill.AttachmentName = !string.IsNullOrEmpty(email.MatchedAttachmentName) ? email.MatchedAttachmentName : "Invoice.pdf";
            }
            _db.Bills.Save(bill);

            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
            email.LinkedBillId = bill.Id;
            email.ProcessedByUserId = userId;
            email.ProcessedAt = DateTime.UtcNow;
            _db.EmailMessages.Save(email);
        }
    }

    /// <summary>
    /// Gets the full email body HTML for a billing email.
    /// If the local raw MIME cache is missing (e.g. historical ingest), attempts on-demand retrieval from IMAP.
    /// </summary>
    public async Task<string> GetOrFetchEmailBodyAsync(EmailMessage email, CancellationToken cancellationToken = default)
    {
        if (email == null) return string.Empty;

        var mimePath = _cacheMaintenance != null
            ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime", $"{email.Id}.eml")
            : null;

        // 1. If local MIME file exists, extract and return
        if (!string.IsNullOrEmpty(mimePath) && File.Exists(mimePath))
        {
            try
            {
                var body = await EmailCacheMaintenanceService.ExtractBodyFromMimeAsync(mimePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(body)) return body;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract body from local MIME {EmailId}", email.Id);
            }
        }

        // 1b. If legacy body file exists, read and return
        if (_cacheMaintenance != null)
        {
            var legacyHtmlPath = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Bodies", $"{email.Id}.html");
            if (File.Exists(legacyHtmlPath))
            {
                try
                {
                    var body = await EmailCacheMaintenanceService.ReadHtmlWithEncodingDetectionAsync(legacyHtmlPath, _logger, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(body)) return body;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to read legacy body for {EmailId}", email.Id);
                }
            }
        }

        // 2. Local MIME missing or empty - attempt on-demand retrieval from IMAP if credentials configured
        var settings = GetSettings();
        if (settings.Enabled && !string.IsNullOrWhiteSpace(settings.EmailAddress) && email.UniqueId > 0)
        {
            var (client, error) = await ConnectImapClientAsync(settings, cancellationToken);
            if (client != null)
            {
                try
                {
                    using (client)
                    {
                        var folderName = string.IsNullOrWhiteSpace(settings.FolderToMonitor) ? "INBOX" : settings.FolderToMonitor;
                        var folder = await client.GetFolderAsync(folderName, cancellationToken);
                        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                        var mimeMessage = await folder.GetMessageAsync(new UniqueId(email.UniqueId), cancellationToken);
                        if (mimeMessage != null)
                        {
                            if (!string.IsNullOrEmpty(mimePath))
                            {
                                var dir = Path.GetDirectoryName(mimePath);
                                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                await using var fs = File.Create(mimePath);
                                await mimeMessage.WriteToAsync(fs, cancellationToken);
                            }

                            if (_cacheMaintenance != null)
                            {
                                var attachmentFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Attachments", email.Id);
                                Directory.CreateDirectory(attachmentFolder);
                                foreach (var part in mimeMessage.Attachments.OfType<MimePart>())
                                {
                                    var fileName = part.FileName ?? "attachment";
                                    var existingAtt = email.Attachments.FirstOrDefault(a => a.FileName == fileName);
                                    var attId = existingAtt?.Id ?? Guid.NewGuid().ToString();
                                    var attPath = Path.Combine(attachmentFolder, attId);
                                    if (part.Content != null)
                                    {
                                        await using var afs = File.Create(attPath);
                                        await part.Content.DecodeToAsync(afs, cancellationToken);
                                    }
                                }
                            }

                            await client.DisconnectAsync(true, cancellationToken);

                            if (!string.IsNullOrEmpty(mimePath) && File.Exists(mimePath))
                            {
                                return await EmailCacheMaintenanceService.ExtractBodyFromMimeAsync(mimePath, cancellationToken);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "On-demand IMAP body fetch failed for billing email {EmailId} (UID: {Uid})", email.Id, email.UniqueId);
                }
            }
        }

        // 3. Fallback to snippet if body unavailable
        if (!string.IsNullOrWhiteSpace(email.Snippet))
        {
            return $"<div style=\"white-space:pre-wrap;font-family:sans-serif;padding:16px;\">{System.Net.WebUtility.HtmlEncode(email.Snippet)}</div>";
        }

        return string.Empty;
    }

    /// <summary>
    /// Re-evaluates an email through the bill extraction and decision pipeline from scratch.
    /// Erases any existing draft bills associated with the email to prevent duplicates.
    /// If an existing linked bill is already approved, returns RequiresConfirmation=true unless forceEvenIfApproved is true.
    /// </summary>
    public async Task<ReprocessEmailResult> ReprocessEmailAsync(string emailMessageId, string userId, bool forceEvenIfApproved = false, CancellationToken cancellationToken = default)
    {
        var email = _db.EmailMessages.GetById(emailMessageId);
        if (email == null)
        {
            return new ReprocessEmailResult { Success = false, ErrorMessage = "Email message not found." };
        }

        // 1. Gather bills that originated from this email (never delete pre-existing bills that were merely linked as duplicates)
        var linkedIds = new HashSet<string>(email.LinkedBillIds);
        if (!string.IsNullOrEmpty(email.LinkedBillId)) linkedIds.Add(email.LinkedBillId);

        // Also check if any bills in the database explicitly reference this email
        var allBills = _db.Bills.GetAll();
        foreach (var b in allBills.Where(b => b.SourceEmailId == email.Id))
        {
            linkedIds.Add(b.Id);
        }

        var billsFromThisEmail = linkedIds
            .Select(id => _db.Bills.GetById(id))
            .Where(b => b != null && b.SourceEmailId == email.Id)
            .Select(b => b!)
            .ToList();

        var approvedBills = billsFromThisEmail.Where(b => b.Status != BillStatus.AwaitingApproval).ToList();
        if (approvedBills.Count > 0 && !forceEvenIfApproved)
        {
            return new ReprocessEmailResult
            {
                Success = false,
                RequiresConfirmation = true,
                ApprovedBillsCount = approvedBills.Count,
                FinalStatus = email.BillingStatus,
                LinkedBills = billsFromThisEmail,
                ErrorMessage = $"This email is linked to {approvedBills.Count} approved bill(s)."
            };
        }

        // 2. Erase bills created by this email (both draft and approved if force-confirmed), keeping pre-existing bills intact
        foreach (var bill in billsFromThisEmail)
        {
            try
            {
                _db.Bills.Delete(bill.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete previous bill {BillId} during re-processing", bill.Id);
            }
        }
        email.LinkedBillId = null;
        email.LinkedBillIds.Clear();

        // 3. Ensure local MIME cache exists (fetching on-demand from IMAP if missing)
        MimeMessage? mimeMessage = null;
        var mimePath = _cacheMaintenance != null
            ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime", $"{email.Id}.eml")
            : null;

        if (!string.IsNullOrEmpty(mimePath) && File.Exists(mimePath))
        {
            try
            {
                using var fs = File.OpenRead(mimePath);
                mimeMessage = await MimeMessage.LoadAsync(fs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load local MIME {MimePath}", mimePath);
            }
        }

        if (mimeMessage == null && email.UniqueId > 0)
        {
            var settings = GetSettings();
            var (client, _) = await ConnectImapClientAsync(settings, cancellationToken);
            if (client != null)
            {
                try
                {
                    using (client)
                    {
                        var folderName = string.IsNullOrWhiteSpace(settings.FolderToMonitor) ? "INBOX" : settings.FolderToMonitor;
                        var folder = await client.GetFolderAsync(folderName, cancellationToken);
                        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                        mimeMessage = await folder.GetMessageAsync(new UniqueId(email.UniqueId), cancellationToken);
                        if (mimeMessage != null && !string.IsNullOrEmpty(mimePath))
                        {
                            var dir = Path.GetDirectoryName(mimePath);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            await using var fs = File.Create(mimePath);
                            await mimeMessage.WriteToAsync(fs, cancellationToken);
                        }
                        await client.DisconnectAsync(true, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to fetch MIME on-demand for reprocess {EmailId}", email.Id);
                }
            }
        }

        // 4. Extract attachments and inspect with PDF/XML invoice extractor
        var settingsForPipeline = GetSettings();
        var extractionContext = BuildExtractionContext(settingsForPipeline);
        var extractedInvoices = new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>();
        var allInspectedDocs = new List<(ExtractedInvoiceData Extraction, string SavedPath, string FileName)>();
        string? fallbackAttachmentPath = null;
        string? fallbackAttachmentName = null;

        if (mimeMessage != null)
        {
            var attachmentFolder = _cacheMaintenance != null
                ? Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Attachments", email.Id)
                : null;
            if (!string.IsNullOrEmpty(attachmentFolder)) Directory.CreateDirectory(attachmentFolder);

            var attachments = mimeMessage.Attachments.OfType<MimePart>().ToList();
            email.Attachments.Clear();

            foreach (var part in attachments)
            {
                var fileName = part.FileName ?? "attachment";
                var ext = Path.GetExtension(fileName).ToLowerInvariant();

                using var mem = new MemoryStream();
                if (part.Content != null)
                {
                    await part.Content.DecodeToAsync(mem, cancellationToken);
                    mem.Position = 0;
                }

                var uploadBillId = Guid.NewGuid().ToString();
                var savedPath = await _fileService.UploadStreamAsync("bills", uploadBillId, mem, fileName);
                var attachmentId = Guid.NewGuid().ToString();

                if (!string.IsNullOrEmpty(attachmentFolder))
                {
                    try
                    {
                        var cachedFilePath = Path.Combine(attachmentFolder, attachmentId);
                        mem.Position = 0;
                        await using var fileStream = File.Create(cachedFilePath);
                        await mem.CopyToAsync(fileStream, cancellationToken);
                    }
                    catch { }
                }

                email.Attachments.Add(new EmailAttachmentMeta
                {
                    Id = attachmentId,
                    FileName = fileName,
                    ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                    Size = mem.Length
                });
                email.HasAttachments = true;

                if (ext == ".pdf" || ext == ".xml")
                {
                    mem.Position = 0;
                    var extracted = _pdfInspectionService.Inspect(mem, fileName, extractionContext);
                    if (extracted != null)
                    {
                        allInspectedDocs.Add((extracted, savedPath, fileName));
                        if (extracted.TotalAmount != 0 && !extracted.IsAccountStatement && !extracted.IsPaymentReceipt)
                        {
                            extractedInvoices.Add((extracted, savedPath, fileName));
                        }
                    }
                }

                if (fallbackAttachmentPath == null && (ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg"))
                {
                    fallbackAttachmentPath = savedPath;
                    fallbackAttachmentName = fileName;
                }
            }
        }
        else if (!string.IsNullOrEmpty(email.MatchedAttachmentPath))
        {
            try
            {
                var parts = email.MatchedAttachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    var category = parts[2];
                    var contextId = parts[3];
                    var fileName = parts[4];
                    using var stream = _fileService.OpenRead(category, contextId, fileName);
                    if (stream != null)
                    {
                        using var mem = new MemoryStream();
                        await stream.CopyToAsync(mem);
                        mem.Position = 0;
                        var extracted = _pdfInspectionService.Inspect(mem, email.MatchedAttachmentName ?? fileName, extractionContext);
                        if (extracted != null)
                        {
                            allInspectedDocs.Add((extracted, email.MatchedAttachmentPath, email.MatchedAttachmentName ?? fileName));
                            if (extracted.TotalAmount != 0 && !extracted.IsAccountStatement && !extracted.IsPaymentReceipt)
                            {
                                extractedInvoices.Add((extracted, email.MatchedAttachmentPath, email.MatchedAttachmentName ?? fileName));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to re-inspect existing attachment {Path}", email.MatchedAttachmentPath);
            }
        }

        email.MatchedAttachmentPath = extractedInvoices.Count > 0 ? extractedInvoices[0].SavedPath : (fallbackAttachmentPath ?? email.MatchedAttachmentPath);
        email.MatchedAttachmentName = extractedInvoices.Count > 0 ? extractedInvoices[0].FileName : (fallbackAttachmentName ?? email.MatchedAttachmentName);

        if (extractedInvoices.Count > 0)
        {
            email.DetectedAmount = extractedInvoices[0].Extraction.TotalAmount;
            email.DetectedPaidAmount = extractedInvoices[0].Extraction.PaidAmount > 0 ? extractedInvoices[0].Extraction.PaidAmount : null;
            email.DetectedBalanceDue = extractedInvoices[0].Extraction.BalanceDue > 0 ? extractedInvoices[0].Extraction.BalanceDue : null;
            email.DetectedCurrency = extractedInvoices[0].Extraction.Currency;
        }
        else
        {
            var textAmount = ExtractAmountFromText($"{email.Subject} {email.Snippet}");
            if (textAmount > 0)
            {
                email.DetectedAmount = textAmount;
                email.DetectedCurrency = settingsForPipeline.DefaultCurrency;
            }
        }

        // 5. Decision pipeline
        var createdBills = new List<Bill>();

        // Check spam
        if (settingsForPipeline.IgnoredSenderDomains.Any(d => !string.IsNullOrWhiteSpace(d) &&
            (email.FromAddress.Contains(d, StringComparison.OrdinalIgnoreCase) || email.FromName.Contains(d, StringComparison.OrdinalIgnoreCase))))
        {
            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Dismissed;
        }
        else
        {
            if (IsAccountStatement(email, allInspectedDocs, settingsForPipeline))
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Statement;
            }
            else if (IsPaymentReceiptEmail(email, allInspectedDocs, settingsForPipeline))
            {
                email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt;
            }
            else
            {
                var primaryExt = extractedInvoices.FirstOrDefault().Extraction;
                var supplier = FindSupplier(email.FromAddress, email.FromName, primaryExt?.SupplierName, primaryExt?.SupplierEmail, primaryExt?.SupplierTaxId);

                if (extractedInvoices.Count > 0)
                {
                    bool hasDuplicate = false;
                    var batchEvaluations = EvaluateExtractedInvoicesBatch(email, extractedInvoices, supplier, settingsForPipeline);

                    foreach (var (extraction, path, fileName, itemSupplier, poMatch, existingMatch) in batchEvaluations)
                    {
                        if (existingMatch != null)
                        {
                            hasDuplicate = true;
                            if (!email.LinkedBillIds.Contains(existingMatch.Bill.Id)) email.LinkedBillIds.Add(existingMatch.Bill.Id);
                            continue;
                        }

                        var draftBill = BuildDraftBillFromExtraction(
                            email,
                            extraction,
                            itemSupplier,
                            poMatch,
                            path,
                            fileName,
                            settingsForPipeline,
                            autoIngested: true);

                        _db.Bills.Save(draftBill);
                        createdBills.Add(draftBill);
                        if (!email.LinkedBillIds.Contains(draftBill.Id)) email.LinkedBillIds.Add(draftBill.Id);
                    }

                    if (createdBills.Count > 0)
                    {
                        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                        email.LinkedBillId = createdBills[0].Id;
                        email.LinkedPoId = createdBills[0].PurchaseOrderId;
                    }
                    else if (hasDuplicate)
                    {
                        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                        email.LinkedBillId = email.LinkedBillIds.FirstOrDefault();
                    }
                }
                else
                {
                    bool handledByRecurring = false;
                    if (settingsForPipeline.AutoMatchRecurringExpenses && settingsForPipeline.AllowDraftWithoutPdfForRecurring)
                    {
                        var recurring = FindMatchingRecurringExpense(supplier?.Name ?? email.FromName);
                        if (recurring != null)
                        {
                            var parsedAmount = ExtractAmountFromText($"{email.Subject} {email.Snippet}");
                            if (parsedAmount > 0)
                            {
                                handledByRecurring = true;
                                var existingRecurringMatch = FindExistingBillMatch(
                                    extraction: null,
                                    supplierId: supplier?.Id,
                                    supplierName: recurring.Supplier,
                                    emailSubject: email.Subject,
                                    emailSnippet: email.Snippet,
                                    matchedPoId: null,
                                    currentEmailId: email.Id,
                                    duplicateWindowDays: settingsForPipeline.DuplicateDateWindowDays,
                                    textDetectedAmount: parsedAmount);

                                if (existingRecurringMatch != null)
                                {
                                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                                    email.LinkedBillId = existingRecurringMatch.Bill.Id;
                                    email.LinkedBillIds = [existingRecurringMatch.Bill.Id];
                                }
                                else
                                {
                                    var draftBill = new Bill
                                    {
                                        Id = Guid.NewGuid().ToString(),
                                        SupplierId = supplier?.Id ?? string.Empty,
                                        SupplierName = recurring.Supplier,
                                        Description = $"Recurring: {recurring.Name} (Amount from email)",
                                        Date = email.Date.DateTime,
                                        DueDate = email.Date.DateTime.AddDays(30),
                                        Amount = parsedAmount,
                                        Currency = settingsForPipeline.DefaultCurrency,
                                        LinkedRecurringExpenseId = recurring.Id,
                                        Status = BillStatus.AwaitingApproval,
                                        AttachmentPath = "",
                                        AttachmentName = "",
                                        AutoIngested = true,
                                        SourceEmailId = email.Id,
                                        SourceEmailSubject = email.Subject,
                                        SourceSenderEmail = email.FromAddress,
                                        AutoMatchReason = $"Matched to recurring expense template '{recurring.Name}'"
                                    };

                                    _db.Bills.Save(draftBill);
                                    createdBills.Add(draftBill);
                                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                                    email.LinkedBillId = draftBill.Id;
                                    email.LinkedBillIds = [draftBill.Id];
                                }
                            }
                        }
                    }

                    if (!handledByRecurring)
                    {
                        var existingFallbackMatch = FindExistingBillMatch(
                            extraction: null,
                            supplierId: supplier?.Id,
                            supplierName: supplier?.Name ?? email.FromName,
                            emailSubject: email.Subject,
                            emailSnippet: email.Snippet,
                            matchedPoId: null,
                            currentEmailId: email.Id,
                            duplicateWindowDays: settingsForPipeline.DuplicateDateWindowDays,
                            textDetectedAmount: email.DetectedAmount);

                        if (existingFallbackMatch != null)
                        {
                            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                            email.LinkedBillId = existingFallbackMatch.Bill.Id;
                            email.LinkedBillIds = [existingFallbackMatch.Bill.Id];
                        }
                        else
                        {
                            email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed;
                        }
                    }
                }
            }
        }

        email.ProcessedByUserId = userId;
        _db.EmailMessages.Save(email);

        // Generate matching DecisionLog
        var log = await GenerateDecisionLogAsync(email, allInspectedDocs);
        email.DecisionLog = log;
        _db.EmailMessages.Save(email);

        var allLinkedBills = email.LinkedBillIds
            .Select(id => _db.Bills.GetById(id))
            .Where(b => b != null)
            .Select(b => b!)
            .ToList();

        return new ReprocessEmailResult
        {
            Success = true,
            FinalStatus = email.BillingStatus,
            LinkedBills = allLinkedBills
        };
    }

    /// <summary>
    /// Force re-downloads messages from the IMAP server.
    /// Repairs missing .eml body caches and attachments for existing emails, and ingests any newly discovered messages.
    /// </summary>
    public async Task<(int RefreshedCount, int NewCount)> ForceRedownloadAsync(int maxMessages = 50, CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.EmailAddress))
        {
            return (0, 0);
        }

        var (client, error) = await ConnectImapClientAsync(settings, cancellationToken);
        if (client == null)
        {
            throw new InvalidOperationException($"IMAP connection failed: {error}");
        }

        int refreshedCount = 0;
        int newCount = 0;

        try
        {
            using (client)
            {
                var folderName = string.IsNullOrWhiteSpace(settings.FolderToMonitor) ? "INBOX" : settings.FolderToMonitor;
                var folder = await client.GetFolderAsync(folderName, cancellationToken);
                await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

                var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, cancellationToken);
                var recentUids = uids.OrderByDescending(u => u.Id).Take(maxMessages).ToList();

                foreach (var uid in recentUids)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var existing = _db.EmailMessages.GetByUniqueId(CompanyBillInboxConstants.SystemEmployeeId, uid.Id);

                    var mimeMessage = await folder.GetMessageAsync(uid, cancellationToken);
                    if (mimeMessage == null) continue;

                    if (existing != null)
                    {
                        // Cache MIME message
                        if (_cacheMaintenance != null)
                        {
                            var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime");
                            Directory.CreateDirectory(mimeFolder);
                            var mimePath = Path.Combine(mimeFolder, $"{existing.Id}.eml");
                            await using var mimeStream = File.Create(mimePath);
                            await mimeMessage.WriteToAsync(mimeStream, cancellationToken);
                        }

                        // Update metadata
                        existing.Subject = mimeMessage.Subject ?? existing.Subject;
                        existing.Date = mimeMessage.Date;
                        existing.FromName = mimeMessage.From.Mailboxes.FirstOrDefault()?.Name ?? existing.FromName;
                        existing.FromAddress = mimeMessage.From.Mailboxes.FirstOrDefault()?.Address ?? existing.FromAddress;

                        var textPreview = mimeMessage.TextBody;
                        if (string.IsNullOrWhiteSpace(textPreview) && !string.IsNullOrWhiteSpace(mimeMessage.HtmlBody))
                        {
                            textPreview = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(mimeMessage.HtmlBody, "<.*?>", " "));
                        }
                        textPreview = (textPreview ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                        existing.Snippet = textPreview.Length > 100 ? textPreview[..100] : textPreview;

                        _db.EmailMessages.Save(existing);

                        // If still Unprocessed, reprocess with newly cached content
                        if (existing.BillingStatus == CompanyBillInboxConstants.BillingStatus.Unprocessed || string.IsNullOrEmpty(existing.BillingStatus))
                        {
                            await ReprocessEmailAsync(existing.Id, "system", forceEvenIfApproved: false, cancellationToken);
                        }

                        refreshedCount++;
                    }
                    else
                    {
                        var newEmail = await ProcessMimeMessageAsync(mimeMessage, uid.Id, cancellationToken);
                        if (newEmail != null) newCount++;
                    }
                }

                await client.DisconnectAsync(true, cancellationToken);

                settings.LastCheckedAt = DateTime.UtcNow;
                settings.LastCheckStatus = "Success";
                SaveSettings(settings);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Force redownload failed for company bill inbox");
            throw;
        }

        return (refreshedCount, newCount);
    }

    private static bool ContainsAnyKeyword(string text, IEnumerable<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private RecurringExpense? FindMatchingRecurringExpense(string? candidateName)
    {
        if (string.IsNullOrWhiteSpace(candidateName)) return null;
        var all = _db.RecurringExpenses.GetAll();
        return all.FirstOrDefault(r =>
            SupplierNamesMatch(r.Supplier, candidateName) ||
            SupplierNamesMatch(r.Name, candidateName));
    }

    private static decimal ExtractAmountFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var regex = new System.Text.RegularExpressions.Regex(@"(?:[\$€£]|CAD|USD)?\s*(\d{1,6}(?:[.,]\d{2}))\s*(?:[\$€£]|CAD|USD)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = regex.Matches(text);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var raw = match.Groups[1].Value.Replace(',', '.');
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) && val > 0)
            {
                return val;
            }
        }
        return 0;
    }
    /// <summary>
    /// Scans unprocessed and auto-drafted bill inbox emails to identify matching bills that were already
    /// entered manually into Spokes (e.g. before the mailbox was enabled). Links them to existing bills,
    /// sets status to DuplicateDetected, and cleans up unapproved redundant drafts.
    /// </summary>
    public async Task<DuplicateScanResult> ScanForDuplicateBillsAsync(CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        var result = new DuplicateScanResult();

        // Get candidate emails in the Bill Inbox
        var candidateEmails = _db.EmailMessages.GetAllForEmployee(CompanyBillInboxConstants.SystemEmployeeId)
            .Where(e => e.BillingStatus == CompanyBillInboxConstants.BillingStatus.Unprocessed ||
                        e.BillingStatus == CompanyBillInboxConstants.BillingStatus.AutoDrafted)
            .ToList();

        result.ScannedEmailsCount = candidateEmails.Count;

        foreach (var email in candidateEmails)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (email.BillingStatus == CompanyBillInboxConstants.BillingStatus.AutoDrafted && email.LinkedBillIds.Count > 0)
            {
                bool anyDraftCleanedUp = false;
                var excludedIds = new HashSet<string>(email.LinkedBillIds);
                foreach (var draftId in email.LinkedBillIds.ToList())
                {
                    var draftBill = _db.Bills.GetById(draftId);
                    if (draftBill == null || !draftBill.AutoIngested || draftBill.Status != BillStatus.AwaitingApproval)
                    {
                        continue;
                    }

                    var reconstructedExtraction = new ExtractedInvoiceData
                    {
                        SupplierName = draftBill.SupplierName,
                        InvoiceNumber = draftBill.InvoiceNumber,
                        InvoiceDate = draftBill.Date,
                        DueDate = draftBill.DueDate,
                        TotalAmount = draftBill.Amount,
                        SubTotal = draftBill.SubTotal,
                        TaxAmount = draftBill.TaxAmount,
                        Currency = draftBill.Currency,
                        IsCreditNote = draftBill.IsCreditNote
                    };

                    var draftMatch = FindExistingBillMatch(
                        extraction: reconstructedExtraction,
                        supplierId: draftBill.SupplierId,
                        supplierName: draftBill.SupplierName,
                        emailSubject: email.Subject,
                        emailSnippet: email.Snippet,
                        matchedPoId: draftBill.PurchaseOrderId,
                        currentEmailId: email.Id,
                        duplicateWindowDays: settings.DuplicateDateWindowDays,
                        textDetectedAmount: draftBill.Amount,
                        excludeBillIds: excludedIds);

                    if (draftMatch != null)
                    {
                        result.DuplicatesFoundCount++;
                        result.DraftsCleanedUpCount++;
                        anyDraftCleanedUp = true;

                        // Delete only this specific redundant draft
                        _db.Bills.Delete(draftBill.Id);
                        email.LinkedBillIds.Remove(draftBill.Id);
                        excludedIds.Add(draftMatch.Bill.Id);
                        if (!email.LinkedBillIds.Contains(draftMatch.Bill.Id))
                        {
                            email.LinkedBillIds.Add(draftMatch.Bill.Id);
                        }
                    }
                }

                if (anyDraftCleanedUp)
                {
                    var remainingDrafts = email.LinkedBillIds
                        .Select(id => _db.Bills.GetById(id))
                        .OfType<Bill>()
                        .Where(b => b.Status == BillStatus.AwaitingApproval && b.SourceEmailId == email.Id)
                        .ToList();

                    if (remainingDrafts.Count > 0)
                    {
                        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted;
                        email.LinkedBillId = remainingDrafts[0].Id;
                    }
                    else
                    {
                        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                        email.LinkedBillId = email.LinkedBillIds.FirstOrDefault();
                    }

                    try
                    {
                        var log = await GenerateDecisionLogAsync(email);
                        email.DecisionLog = log;
                    }
                    catch { }

                    _db.EmailMessages.Save(email);
                }
            }
            else
            {
                // Unprocessed email (no draft bills created yet)
                ExtractedInvoiceData? extraction = null;
                if (!string.IsNullOrEmpty(email.MatchedAttachmentPath))
                {
                    try
                    {
                        var parts = email.MatchedAttachmentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5)
                        {
                            var category = parts[2];
                            var contextId = parts[3];
                            var fileName = parts[4];
                            using var stream = _fileService.OpenRead(category, contextId, fileName);
                            if (stream != null)
                            {
                                using var mem = new MemoryStream();
                                await stream.CopyToAsync(mem, cancellationToken);
                                mem.Position = 0;
                                extraction = _pdfInspectionService.Inspect(mem, email.MatchedAttachmentName ?? fileName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to read attachment for duplicate scan on email {EmailId}", email.Id);
                    }
                }

                var supplier = FindSupplier(email.FromAddress, email.FromName, extraction?.SupplierName, extraction?.SupplierEmail, extraction?.SupplierTaxId);
                var poMatch = extraction != null ? _poMatchingEngine.FindMatch(extraction, supplier?.Id) : MatchingPoResult.None;

                var match = FindExistingBillMatch(
                    extraction,
                    supplier?.Id,
                    supplier?.Name ?? extraction?.SupplierName ?? email.FromName,
                    email.Subject,
                    email.Snippet,
                    poMatch?.MatchedPo?.Id,
                    email.Id,
                    settings.DuplicateDateWindowDays,
                    textDetectedAmount: email.DetectedAmount,
                    excludeBillIds: email.LinkedBillIds);

                if (match != null)
                {
                    result.DuplicatesFoundCount++;
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected;
                    email.LinkedBillId = match.Bill.Id;
                    email.LinkedBillIds = [match.Bill.Id];

                    try
                    {
                        var log = await GenerateDecisionLogAsync(email);
                        email.DecisionLog = log;
                    }
                    catch { }

                    _db.EmailMessages.Save(email);
                }
            }
        }

        return result;
    }
}

public class DuplicateScanResult
{
    public int ScannedEmailsCount { get; set; }
    public int DuplicatesFoundCount { get; set; }
    public int DraftsCleanedUpCount { get; set; }
}

public class ReprocessEmailResult
{
    public bool Success { get; set; }
    public bool RequiresConfirmation { get; set; }
    public int ApprovedBillsCount { get; set; }
    public string? FinalStatus { get; set; }
    public List<Bill> LinkedBills { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public class RejectDraftBillOptions
{
    public bool IsDuplicate { get; set; }
    public string? DuplicateOfBillId { get; set; }
    public string? RejectionReason { get; set; }
    public bool AttachDocumentToExistingBill { get; set; } = true;
}


