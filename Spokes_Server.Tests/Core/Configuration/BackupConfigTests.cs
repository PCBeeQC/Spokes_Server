using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Xunit;

namespace Spokes_Server.Tests.Core.Configuration;

public class BackupConfigTests
{
    [Theory]
    [InlineData(BackupDestinationType.LocalMount)]
    [InlineData(BackupDestinationType.S3)]
    [InlineData(BackupDestinationType.GoogleDrive)]
    [InlineData(BackupDestinationType.GitHub)]
    [InlineData(BackupDestinationType.WebDav)]
    [InlineData(BackupDestinationType.Sftp)]
    public void GetDestinationConfig_MapsEveryDestinationTypeAndMutatesUnderlyingConfig(BackupDestinationType type)
    {
        var config = new BackupConfig();

        var destination = config.GetDestinationConfig(type);
        Assert.False(destination.IsEnabled);

        destination.IsEnabled = true;

        bool expected = type switch
        {
            BackupDestinationType.LocalMount => config.SecondaryMount.IsEnabled,
            BackupDestinationType.S3 => config.S3.IsEnabled,
            BackupDestinationType.GoogleDrive => config.GoogleDrive.IsEnabled,
            BackupDestinationType.GitHub => config.GitHub.IsEnabled,
            BackupDestinationType.WebDav => config.WebDav.IsEnabled,
            BackupDestinationType.Sftp => config.Sftp.IsEnabled,
            _ => false
        };

        Assert.True(expected);
    }

    [Fact]
    public void GetDestinationConfig_CoversAllEnumValues()
    {
        var config = new BackupConfig();
        foreach (var type in Enum.GetValues<BackupDestinationType>())
        {
            Assert.NotNull(config.GetDestinationConfig(type));
        }
    }
}
