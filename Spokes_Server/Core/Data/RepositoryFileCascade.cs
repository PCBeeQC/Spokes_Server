using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Data;

/// <summary>
/// Global coordinator that cascades entity deletions to disk files.
/// Listens to entity deletions and ensures any entity implementing IHasAssociatedFiles
/// automatically has all its associated files deleted from physical storage.
/// </summary>
public static class RepositoryFileCascade
{
    private static IFileService? _fileService;
    private static ILogger? _logger;

    public static void Initialize(IFileService fileService, ILogger? logger = null)
    {
        _fileService = fileService;
        _logger = logger;
    }

    public static void HandleEntityDeleted(IHasAssociatedFiles entity)
    {
        if (entity == null || _fileService == null) return;

        // Run deletion asynchronously in background without blocking the synchronous repository call
        _ = Task.Run(async () =>
        {
            try
            {
                var count = await _fileService.DeleteAssociatedFilesAsync(entity);
                if (count > 0)
                {
                    _logger?.LogInformation("RepositoryFileCascade: Automatically deleted {Count} physical file(s) for deleted entity {Type}",
                        count, entity.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "RepositoryFileCascade: Failed to cascade file deletion for entity {Type}",
                    entity.GetType().Name);
            }
        });
    }
}
