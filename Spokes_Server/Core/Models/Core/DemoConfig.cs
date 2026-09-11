using System.Collections.Generic;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Core;

public class DemoConfig : IDataEntity
{
    public string Id { get; set; } = "demo-config";
    public List<string> AutoLoginEmployeeIds { get; set; } = new();
}
