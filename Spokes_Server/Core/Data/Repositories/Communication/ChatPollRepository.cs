namespace Spokes_Server.Core.Data.Repositories.Communication;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// File-backed JSON repository for <see cref="ChatPoll"/> entities.
/// </summary>
public class ChatPollRepository : JsonRepository<ChatPoll>
{
    protected ChatPollRepository()
        : base(null!, "Data", "*.json", null)
    {
        _isLoaded = true;
    }

    public ChatPollRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Polls"), "*.json")
    {
    }

    protected override string GetFilePath(ChatPoll item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public virtual List<ChatPoll> GetByChannel(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return [];
        }

        return GetAll()
            .Where(p => string.Equals(p.ChannelId, channelId, StringComparison.Ordinal))
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
    }
}
