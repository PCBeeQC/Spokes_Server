using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Data;

/// <summary>
/// Per-instance coordinator that cascades entity deletions to disk files.
/// Attached to a <see cref="DiskPersistenceService"/> so each host / database container
/// isolates its own file cascade without shared static state.
/// </summary>
public sealed class RepositoryFileCascade
{
    private readonly IFileService _fileService;
    private readonly ILogger? _logger;

    public RepositoryFileCascade(IFileService fileService, ILogger? logger = null)
    {
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _logger = logger;
    }

    public void HandleEntityDeleted(IHasAssociatedFiles entity)
    {
        if (entity == null) return;

        // Run deletion asynchronously in background without blocking the synchronous repository call
        _ = Task.Run(() => HandleEntityDeletedAsync(entity));
    }

    public async Task<int> HandleEntityDeletedAsync(IHasAssociatedFiles entity)
    {
        if (entity == null) return 0;

        try
        {
            var count = await _fileService.DeleteAssociatedFilesAsync(entity).ConfigureAwait(false);
            if (count > 0)
            {
                _logger?.LogInformation("RepositoryFileCascade: Automatically deleted {Count} physical file(s) for deleted entity {Type}",
                    count, entity.GetType().Name);
            }
            return count;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "RepositoryFileCascade: Failed to cascade file deletion for entity {Type}",
                entity.GetType().Name);
            return 0;
        }
    }
}
