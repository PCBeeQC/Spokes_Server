using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public record PushRelayContext(
    CompanyProfile Profile,
    ServerConfig ServerConfig,
    string DefaultIcon,
    string? OriginUrl);

public class PushPayloadBuilder
{
    public const int CallTtlSeconds = 60;
    public const int CalendarTtlSeconds = 2 * 3600;
    public const int ClearNotificationTtlSeconds = 24 * 3600;
    public const int DefaultTtlSeconds = 7 * 24 * 3600;

    public virtual string GetEffectiveServerName(PushNotificationRequest request, CompanyProfile profile)
    {
        return !string.IsNullOrWhiteSpace(request.ServerName)
            ? request.ServerName
            : (!string.IsNullOrWhiteSpace(profile?.CompanyName) ? profile.CompanyName : "Spokes");
    }

    public virtual string? GetEffectiveThreadId(PushNotificationRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ThreadId)
            ? request.ThreadId
            : (!string.IsNullOrWhiteSpace(request.Tag) ? request.Tag : null);
    }

    public virtual string? GetEffectiveSound(PushNotificationRequest request)
    {
        if (request.IsSilent) return null;
        return request.Sound 
            ?? NotificationCategoryRegistry.GetCategory(request.Category)?.DefaultSoundFileName 
            ?? (request.Category == "chat" ? "spokesnotif1.wav" : "default");
    }

    public virtual int GetEffectiveTtlSeconds(PushNotificationRequest request)
    {
        if (request.TtlSeconds.HasValue)
        {
            return Math.Clamp(request.TtlSeconds.Value, 0, DefaultTtlSeconds);
        }

        if (string.Equals(request.Category, "call", StringComparison.OrdinalIgnoreCase))
        {
            return CallTtlSeconds;
        }

        if (string.Equals(request.Category, "calendar", StringComparison.OrdinalIgnoreCase))
        {
            return CalendarTtlSeconds;
        }

        if (string.Equals(request.Category, "clear_notification", StringComparison.OrdinalIgnoreCase))
        {
            return ClearNotificationTtlSeconds;
        }

        return DefaultTtlSeconds;
    }

    /// <summary>
    /// Computes an RFC 8030 §5.4 compliant Topic header (&lt;= 32 URL-safe base64 chars)
    /// so WebPush servers collapse undelivered notifications within the same category and thread.
    /// </summary>
    public virtual string? GetWebPushTopic(PushNotificationRequest request)
    {
        var effectiveThreadId = GetEffectiveThreadId(request);
        if (string.IsNullOrWhiteSpace(effectiveThreadId))
        {
            return null;
        }

        var category = string.IsNullOrWhiteSpace(request.Category) ? "chat" : request.Category.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{category}:{effectiveThreadId}"));
        // 24 bytes encodes to exact 32 base64url characters without padding
        return WebEncoders.Base64UrlEncode(hash.AsSpan(0, 24));
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public virtual string BuildWebPushPayloadJson(PushNotificationRequest request, CompanyProfile profile, string defaultIcon)
    {
        var effectiveServerName = GetEffectiveServerName(request, profile);
        var effectiveThreadId = GetEffectiveThreadId(request);
        var effectiveSound = GetEffectiveSound(request);
        var sentAtUtc = NormalizeUtc(request.SentAtUtc);

        var payload = new
        {
            title = request.Title,
            body = request.Body,
            icon = request.Icon ?? defaultIcon,
            badge = defaultIcon,
            tag = request.Tag,
            category = request.Category,
            actions = request.Actions,
            isSilent = request.IsSilent,
            sound = effectiveSound,
            data = new
            {
                url = request.Url ?? (request.Category == "calendar" ? "/planning/calendar" : "/chat"),
                category = request.Category,
                threadId = effectiveThreadId,
                serverName = effectiveServerName,
                channelName = request.ChannelName,
                isGroupChat = request.IsGroupChat,
                sentAtUtc = sentAtUtc
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    public virtual RelayPushPayload BuildRelayPayload(
        PushNotificationRequest request,
        DeviceSession sub,
        PushRelayContext context)
    {
        var effectiveServerName = GetEffectiveServerName(request, context.Profile);
        var effectiveThreadId = GetEffectiveThreadId(request);
        var effectiveSound = GetEffectiveSound(request);
        var sentAtUtc = NormalizeUtc(request.SentAtUtc);

        string? absoluteImageUrl = null;
        if (!string.IsNullOrEmpty(request.Icon) && !string.IsNullOrEmpty(context.OriginUrl))
        {
            absoluteImageUrl = $"{context.OriginUrl}{request.Icon}";
        }

        var payload = CreateBaseRelayPayload(request, sub, context, effectiveServerName, effectiveThreadId, effectiveSound);
        payload.TtlSeconds = GetEffectiveTtlSeconds(request);

        bool isClearNotification = string.Equals(request.Category, "clear_notification", StringComparison.OrdinalIgnoreCase);

        if (isClearNotification)
        {
            payload.Title = string.Empty;
            payload.Body = string.Empty;
            payload.ServerName = null;
            payload.ChannelName = null;
            payload.OriginUrl = null;
            payload.ServerIconUrl = null;
            payload.SentAtUtc = null;
        }
        else if (!string.IsNullOrEmpty(sub.PushPublicKey))
        {
            var innerPayload = new
            {
                Title = request.Title,
                Body = request.Body,
                Url = request.Url ?? (request.Category == "calendar" ? "/planning/calendar" : "/chat"),
                Category = request.Category,
                ImageUrl = absoluteImageUrl,
                OriginUrl = context.OriginUrl,
                ThreadId = effectiveThreadId,
                ServerName = effectiveServerName,
                ChannelName = request.ChannelName,
                IsGroupChat = request.IsGroupChat,
                ServerIconUrl = payload.ServerIconUrl,
                IsSilent = request.IsSilent,
                Sound = effectiveSound,
                SentAtUtc = sentAtUtc
            };
            var innerPayloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(innerPayload));
            var (encryptedKey, nonce, ciphertext, tag) = EncryptInnerPayload(innerPayloadBytes, sub.PushPublicKey);

            payload.IsEncrypted = true;
            payload.EncryptedKey = encryptedKey;
            payload.Nonce = nonce;
            payload.Ciphertext = ciphertext;
            payload.Tag = tag;

            // Clear sensitive metadata from outer envelope so relay and APNs/FCM cannot read them
            payload.ServerName = null;
            payload.ChannelName = null;
            payload.OriginUrl = null;
            payload.ServerIconUrl = null;
            payload.SentAtUtc = null;
        }
        else
        {
            payload.Title = request.Title;
            payload.Body = request.Body;
            payload.Url = request.Url ?? (request.Category == "calendar" ? "/planning/calendar" : "/chat");
            payload.ImageUrl = absoluteImageUrl;
            payload.SentAtUtc = sentAtUtc;
        }

        return payload;
    }

    private static RelayPushPayload CreateBaseRelayPayload(
        PushNotificationRequest request,
        DeviceSession sub,
        PushRelayContext context,
        string effectiveServerName,
        string? effectiveThreadId,
        string? effectiveSound)
    {
        return new RelayPushPayload
        {
            Token = sub.PushEndpoint ?? string.Empty,
            Category = request.Category,
            Sound = effectiveSound,
            ServerId = context.ServerConfig.DatabaseCreationId,
            DatabaseCreationVersion = context.ServerConfig.DatabaseCreationVersion,
            ServerFingerprint = RelayTrialTicketService.ComputeFingerprint(context.ServerConfig.ServerMasterRsaPublicKey),
            TrialTicket = RelayTrialTicketService.GetTicketForCurrentKey(context.ServerConfig),
            LicensePayload = context.Profile.LicensePayload,
            ThreadId = effectiveThreadId,
            ServerName = effectiveServerName,
            ChannelName = request.ChannelName,
            IsGroupChat = request.IsGroupChat,
            ServerIconUrl = context.OriginUrl != null ? $"{context.OriginUrl}{context.DefaultIcon}" : context.DefaultIcon,
            Badge = request.Badge,
            IsSilent = request.IsSilent,
            OriginUrl = context.OriginUrl
        };
    }

    private static (string EncryptedKey, string Nonce, string Ciphertext, string Tag) EncryptInnerPayload(
        byte[] innerPayloadBytes,
        string pushPublicKey)
    {
        var aesKey = new byte[32];
        RandomNumberGenerator.Fill(aesKey);
        using var aesGcm = new AesGcm(aesKey, 16);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[innerPayloadBytes.Length];
        var authTag = new byte[16];

        aesGcm.Encrypt(nonce, innerPayloadBytes, ciphertext, authTag);

        using var rsa = RSA.Create();
        var keyBytes = Convert.FromBase64String(pushPublicKey);
        try
        {
            rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPublicKey(keyBytes, out _);
        }
        var encryptedKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);

        return (
            Convert.ToBase64String(encryptedKey),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(authTag)
        );
    }
}
