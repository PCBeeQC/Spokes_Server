namespace Spokes_Server.Core.Models.Projects;

public class SubTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> NameTranslations { get; set; } = [];
}



