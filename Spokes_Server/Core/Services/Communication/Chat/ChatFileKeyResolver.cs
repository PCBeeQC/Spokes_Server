namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

public class ChatFileKeyResolver : IFileKeyResolver
{
    private readonly ChatChannelRepository _channels;
    private readonly ScopedKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly ILogger<ChatFileKeyResolver>? _logger;

    public string Category => "chat";

    public ChatFileKeyResolver(
        ChatChannelRepository channels,
        ScopedKeystoreService keystore,
        ICryptoService crypto,
        ILogger<ChatFileKeyResolver>? logger = null)
    {
        _channels = channels;
        _keystore = keystore;
        _crypto = crypto;
        _logger = logger;
    }

    public FileKeyResolutionResult ResolveUploadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        bool isThumbnail = false,
        HttpContext? httpContext = null)
    {
        var res = ResolveChannelKey(contextId, user, tokenEncryptionKey, httpContext);
        if (!res.IsSuccess)
        {
            return isThumbnail
                ? FileKeyResolutionResult.Failure(res.ErrorMessage ?? "You do not have the encryption key for this channel.")
                : FileKeyResolutionResult.Failure("You do not have the encryption key for this channel (missing token).");
        }
        return res;
    }

    public FileKeyResolutionResult ResolveDownloadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        HttpContext? httpContext = null)
    {
        var res = ResolveChannelKey(contextId, user, tokenEncryptionKey, httpContext);
        if (!res.IsSuccess && res.ErrorMessage != "Failed to decrypt channel key.")
        {
            return FileKeyResolutionResult.Failure("You do not have the encryption key for this channel.");
        }
        return res;
    }

    public FileKeyResolutionResult ResolveChannelKey(string channelId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null)
    {
        if (!string.IsNullOrEmpty(tokenEncryptionKey))
            return FileKeyResolutionResult.Success(tokenEncryptionKey);

        var channel = _channels.GetById(channelId);
        if (channel == null || !channel.IsEncrypted)
            return FileKeyResolutionResult.NotEncrypted();

        if (!_keystore.IsUnlocked && httpContext != null && !string.IsNullOrEmpty(user.EncryptedPrivateKey))
        {
            _keystore.TryInitializeFromVaultCookie(httpContext, user.Id, user.EncryptedPrivateKey);
        }

        if (!_keystore.IsUnlocked)
        {
            return FileKeyResolutionResult.Failure("Your chat vault is locked.");
        }

        var privateKey = _keystore.GetPrivateKey();
        if (privateKey == null || !channel.EncryptedChannelKeys.TryGetValue(user.Id, out var cipherKey))
        {
            return FileKeyResolutionResult.Failure("You do not have the encryption key for this channel.");
        }

        try
        {
            var plainKey = _crypto.DecryptRsa(cipherKey, privateKey);
            return FileKeyResolutionResult.Success(plainKey);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to decrypt channel key for user {UserId} in channel {ChannelId}", user.Id, channelId);
            return FileKeyResolutionResult.Failure("Failed to decrypt channel key.");
        }
    }
}
