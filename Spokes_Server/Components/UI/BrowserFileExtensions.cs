using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Forms;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Components.UI;

/// <summary>
/// UI extension methods bridging Blazor IBrowserFile to Core models and stream primitives.
/// </summary>
public static class BrowserFileExtensions
{
    public static async Task<string> UploadAsync(
        this IFileService fileService,
        string category,
        string contextId,
        IBrowserFile file,
        long maxAllowedSize = 50 * 1024 * 1024)
    {
        using var stream = file.OpenReadStream(maxAllowedSize);
        return await fileService.UploadStreamAsync(category, contextId, stream, file.Name);
    }

    public static async Task<EmailAttachment> ToEmailAttachmentAsync(
        this IBrowserFile file,
        long maxAllowedSize = 10 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        await using (var stream = file.OpenReadStream(maxAllowedSize))
        {
            await stream.CopyToAsync(ms, cancellationToken);
        }
        return EmailAttachment.FromBytes(file.Name, ms.ToArray(), file.ContentType);
    }

    public static async Task<List<EmailAttachment>> ToEmailAttachmentsAsync(
        this IEnumerable<IBrowserFile> files,
        long maxAllowedSize = 10 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var list = new List<EmailAttachment>();
        foreach (var file in files)
        {
            list.Add(await file.ToEmailAttachmentAsync(maxAllowedSize, cancellationToken));
        }
        return list;
    }
}
