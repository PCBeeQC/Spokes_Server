namespace Spokes_Server.Core.Data.Repositories.Communication;

using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Data;

public class ChatCategoryRepository : JsonRepository<ChatCategory>
{
    public ChatCategoryRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, System.IO.Path.Combine(config["DataPath"] ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data"), "chat-categories"), "*.json")
    {
    }

    protected override string GetFilePath(ChatCategory item)
    {
        return System.IO.Path.Combine(_basePath, $"{item.Id}.json");
    }

    public void EnsureDefaultCategories()
    {
        var defaults = new[]
        {
            new { Id = "sys_public", Name = "Public", Order = -600 },
            new { Id = "sys_projects", Name = "Projects", Order = -500 },
            new { Id = "sys_teams", Name = "Teams", Order = -400 },
            new { Id = "sys_groups", Name = "Group Chats", Order = -300 },
            new { Id = "sys_direct", Name = "Direct Messages", Order = -200 },
            new { Id = "sys_archive", Name = "Archived", Order = 10000 }
        };

        foreach (var def in defaults)
        {
            if (GetById(def.Id) == null)
            {
                Save(new ChatCategory
                {
                    Id = def.Id,
                    Name = def.Name,
                    DisplayOrder = def.Order,
                    IsSystem = true
                });
            }
        }
    }
}
