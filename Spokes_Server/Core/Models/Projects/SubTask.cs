namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

public class SubTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> NameTranslations { get; set; } = new();
}



