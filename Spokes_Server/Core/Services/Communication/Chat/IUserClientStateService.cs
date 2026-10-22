namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Service interface defining client-side UI and state coordination: in-memory drafts, active channel tracking, and typing indicators.
/// </summary>
public interface IUserClientStateService
{
    void UpdateDraftInMemory(string userId, string channelId, string draftText);
    void MarkDraftAsAutoOpened(string userId, string channelId);
    void UpdateActiveChannel(string userId, string channelId);
    string? GetDraft(string userId, string channelId);
    string? GetActiveDraftChannel(string userId);
    void SetTyping(string userId, string channelId, bool isTyping);
    void StopActiveTyping(string userId);
    void FlushToDiskIfUnsent(string userId);
    void ClearDraft(string userId, string channelId);
}
