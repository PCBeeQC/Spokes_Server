namespace Spokes_Server.Core.Models.Core;

public class DocumentBlockInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty; // e.g., "Signature"
    public string Name { get; set; } = string.Empty; // e.g., "Client Signature"

    // Key-value pairs matching the IDocumentBlock's expected Fields
    public Dictionary<string, string> Properties { get; set; } = new();
}
