using Spokes_Server.Components.UI;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Components.UI;

public class PendingFileTests
{
    [Fact]
    public void PendingFile_Initialization_SetsDefaults()
    {
        var pendingFile = new PendingFile();

        Assert.False(string.IsNullOrEmpty(pendingFile.Id));
        Assert.Equal("", pendingFile.FileName);
        Assert.Null(pendingFile.StageText);
        Assert.False(pendingFile.IsUploading);
        Assert.Null(pendingFile.Attachment);
        Assert.Equal(0.0, pendingFile.UploadProgress);
    }

    [Fact]
    public void PendingFile_Properties_CanBeSet()
    {
        var id = Guid.NewGuid().ToString();
        var fileName = "test.png";
        var stageText = "Uploading...";
        var attachment = new ChatAttachment { FileName = fileName };

        var pendingFile = new PendingFile
        {
            Id = id,
            FileName = fileName,
            StageText = stageText,
            IsUploading = true,
            Attachment = attachment,
            UploadProgress = 75.5
        };

        Assert.Equal(id, pendingFile.Id);
        Assert.Equal(fileName, pendingFile.FileName);
        Assert.Equal(stageText, pendingFile.StageText);
        Assert.True(pendingFile.IsUploading);
        Assert.Equal(attachment, pendingFile.Attachment);
        Assert.Equal(75.5, pendingFile.UploadProgress);
    }
}
