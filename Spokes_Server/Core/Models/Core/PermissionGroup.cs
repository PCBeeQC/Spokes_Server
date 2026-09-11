namespace Spokes_Server.Core.Models.Core;

public class PermissionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}
