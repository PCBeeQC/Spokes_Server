namespace Spokes_Server.Core.Data;

/// <summary>
/// Defines an entity or model that holds references to physical files on disk.
/// Enables universal, automated cascade deletion across repositories and services.
/// </summary>
public interface IHasAssociatedFiles
{
    /// <summary>
    /// Returns all file URLs, relative paths, or physical paths associated with this entity.
    /// </summary>
    IEnumerable<string> GetAssociatedFilePaths();
}
