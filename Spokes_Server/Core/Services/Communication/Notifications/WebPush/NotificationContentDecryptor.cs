using System;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public class NotificationContentDecryptor
{
    private readonly GlobalKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly ILogger<NotificationContentDecryptor>? _logger;

    protected NotificationContentDecryptor()
    {
        _keystore = null!;
        _crypto = null!;
    }

    public NotificationContentDecryptor(
        GlobalKeystoreService keystore,
        ICryptoService crypto,
        ILogger<NotificationContentDecryptor>? logger = null)
    {
        _keystore = keystore ?? throw new ArgumentNullException(nameof(keystore));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _logger = logger;
    }

    public virtual string DecryptAndFormatContent(ChatMessage message, ChatChannel? channel, string userId, int maxLength, bool isReaction = false)
    {
        var rawContent = message.Content;

        if (message.IsEncrypted)
        {
            rawContent = isReaction ? "🔒 Secure Message" : "🔒 New Secure Message";

            if (_keystore.IsUserUnlocked(userId))
            {
                var privateKey = _keystore.GetPrivateKey(userId);
                if (privateKey != null && channel != null && channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherKey))
                {
                    try
                    {
                        var channelKey = _crypto.DecryptRsa(cipherKey, privateKey);
                        if (channelKey != null)
                        {
                            var decrypted = _crypto.DecryptAes(message.Content, channelKey);
                            rawContent = ChatPreviewFormatter.FormatPreviewContent(decrypted, message.Attachments);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to decrypt message body for user {UserId} in notification routing", userId);
                        if (!string.IsNullOrEmpty(message.Content) && !ChatSecurityService.IsPotentialBase64Cipher(message.Content))
                        {
                            rawContent = ChatPreviewFormatter.FormatPreviewContent(message.Content, message.Attachments);
                        }
                    }
                }
            }
        }
        else
        {
            rawContent = ChatPreviewFormatter.FormatPreviewContent(rawContent, message.Attachments);
        }

        if (string.IsNullOrEmpty(rawContent))
        {
            rawContent = "[Attachment]";
        }

        if (rawContent.Length > maxLength)
        {
            rawContent = rawContent.Substring(0, maxLength - 3) + "...";
        }

        return rawContent;
    }
}
