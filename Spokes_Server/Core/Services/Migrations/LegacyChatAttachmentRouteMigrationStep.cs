using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates historical chat message attachment URLs and inline images formatted with legacy 
/// /internal/attachments/ or /spokesapi/attachments/ into the canonical /spokesapi/files/chat/ structure.
/// </summary>
public class LegacyChatAttachmentRouteMigrationStep : IMigrationStep
{
    private readonly CommunicationContext _communication;
    private readonly ILogger<LegacyChatAttachmentRouteMigrationStep>? _logger;

    public string MigrationKey => "20261009_LegacyChatAttachmentRouteMigration";
    public string Description => "Migrates historical chat attachment URLs and inline images to canonical /spokesapi/files/chat/ structure";
    public int Order => 55;

    [ActivatorUtilitiesConstructor]
    public LegacyChatAttachmentRouteMigrationStep(CommunicationContext communicationContext, ILogger<LegacyChatAttachmentRouteMigrationStep>? logger = null)
    {
        _communication = communicationContext;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migratedMessages = 0;
        int migratedAttachments = 0;

        foreach (var message in _communication.ChatMessages.GetAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            bool modified = false;

            if (message.Attachments != null && message.Attachments.Count > 0)
            {
                foreach (var att in message.Attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.FilePath)) continue;

                    string? canonicalPath = CanonicalizeAttachmentPath(att.FilePath);
                    if (canonicalPath != null && canonicalPath != att.FilePath)
                    {
                        att.FilePath = canonicalPath;
                        modified = true;
                        migratedAttachments++;
                    }
                }
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                var originalContent = message.Content;
                bool contentModified = false;
                if (originalContent.Contains("/internal/attachments/"))
                {
                    message.Content = message.Content.Replace("/internal/attachments/", "/spokesapi/files/chat/");
                    contentModified = true;
                }
                if (originalContent.Contains("/spokesapi/attachments/"))
                {
                    message.Content = message.Content.Replace("/spokesapi/attachments/", "/spokesapi/files/chat/");
                    contentModified = true;
                }
                if (contentModified)
                {
                    modified = true;
                }
            }

            if (modified)
            {
                _communication.ChatMessages.Save(message);
                migratedMessages++;
            }
        }

        if (migratedMessages > 0)
        {
            _logger?.LogInformation(
                "[LegacyChatAttachmentRouteMigrationStep] Migrated {MessageCount} message(s) containing {AttachmentCount} legacy attachment URL(s) to canonical /spokesapi/files/chat/ format.",
                migratedMessages, migratedAttachments);
        }

        return Task.CompletedTask;
    }

    public static string? CanonicalizeAttachmentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        const string internalPrefix = "/internal/attachments/";
        const string spokesapiPrefix = "/spokesapi/attachments/";

        if (path.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = path.Substring(internalPrefix.Length).TrimStart('/');
            if (relative.StartsWith("thumb/", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative.Substring("thumb/".Length).TrimStart('/');
            }
            return $"/spokesapi/files/chat/{relative}";
        }

        if (path.StartsWith(spokesapiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = path.Substring(spokesapiPrefix.Length).TrimStart('/');
            if (relative.StartsWith("thumb/", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative.Substring("thumb/".Length).TrimStart('/');
            }
            return $"/spokesapi/files/chat/{relative}";
        }

        return null;
    }
}
