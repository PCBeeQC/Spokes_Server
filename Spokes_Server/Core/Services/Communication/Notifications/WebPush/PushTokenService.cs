using System;
using System.Net;
using Microsoft.AspNetCore.DataProtection;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public class PushTokenService
{
    private readonly IDataProtectionProvider _dataProtection;

    protected PushTokenService()
    {
        _dataProtection = null!;
    }

    public PushTokenService(IDataProtectionProvider dataProtection)
    {
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
    }

    public virtual string CreateAvatarPushToken(string userId)
    {
        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var safeUserId = userId.Replace("|", "");
        var token = protector.Protect($"{safeUserId}|{DateTime.UtcNow.AddHours(48).Ticks}");
        return WebUtility.UrlEncode(token);
    }

    public virtual string GetAvatarUrl(string userId)
    {
        var encodedToken = CreateAvatarPushToken(userId);
        return $"/spokesapi/Media/Avatar/{userId}?t={encodedToken}";
    }

    public virtual string GetDefaultIconUrl(string userId)
    {
        var encodedToken = CreateAvatarPushToken(userId);
        return $"/spokesapi/Media/Icon?t={encodedToken}";
    }
}
