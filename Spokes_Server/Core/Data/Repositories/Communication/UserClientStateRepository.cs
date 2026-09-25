namespace Spokes_Server.Core.Data.Repositories.Communication;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;

public class UserClientStateRepository : JsonRepository<UserClientState>
{
    public UserClientStateRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "clientstates"), "*.json")
    {
    }

    protected override string GetFilePath(UserClientState item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public UserClientState? GetByUserId(string userId) => GetById(userId);

    public string? GetDraft(string userId, string channelId)
    {
        var state = GetById(userId);
        if (state != null && state.ChannelDrafts.TryGetValue(channelId, out var draft) && !string.IsNullOrWhiteSpace(draft))
        {
            return draft;
        }
        return null;
    }

    public void SaveDraft(string userId, string channelId, string draftText)
    {
        var state = GetById(userId) ?? new UserClientState { Id = userId, UserId = userId };
        state.ActiveChannelId = channelId;
        state.LastUpdatedAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(draftText))
        {
            state.ChannelDrafts.Remove(channelId);
            if (state.ChannelDrafts.Count == 0)
            {
                Delete(userId);
                return;
            }
        }
        else
        {
            state.ChannelDrafts[channelId] = draftText;
        }

        Save(state);
    }

    public void ClearDraft(string userId, string channelId)
    {
        var state = GetById(userId);
        if (state != null)
        {
            state.ChannelDrafts.Remove(channelId);
            state.LastUpdatedAt = DateTime.UtcNow;
            if (state.ChannelDrafts.Count == 0)
            {
                Delete(userId);
            }
            else
            {
                Save(state);
            }
        }
    }
}
