using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Spokes_Server.Core.Services.Logging;

namespace Spokes_Server.Tests.Core.Services.Logging;

public class SystemLogServiceTests : IDisposable
{
    private readonly string _testDir;

    public SystemLogServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_LogService_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete test directory: {ex.Message}");
            }
        }
    }

    private static IConfiguration CreateConfig(string dataPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = dataPath
            })
            .Build();
    }

    [Fact]
    public void Constructor_WithDefaultDataPath_CreatesLogsDirectory()
    {
        var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var defaultLogsPath = Path.Combine("Data", "Logs");

        try
        {
            var service = new SystemLogService(emptyConfig);
            Assert.NotNull(service);
            Assert.True(Directory.Exists(defaultLogsPath));
        }
        finally
        {
            if (Directory.Exists(defaultLogsPath))
            {
                try { Directory.Delete(defaultLogsPath, recursive: true); } catch { }
            }
            if (Directory.Exists("Data") && !Directory.EnumerateFileSystemEntries("Data").Any())
            {
                try { Directory.Delete("Data"); } catch { }
            }
        }
    }

    [Fact]
    public void Constructor_WithCustomDataPath_CreatesLogsDirectory()
    {
        var customPath = Path.Combine(_testDir, "CustomData");
        var config = CreateConfig(customPath);

        var service = new SystemLogService(config);

        Assert.NotNull(service);
        var expectedLogsPath = Path.Combine(customPath, "Logs");
        Assert.True(Directory.Exists(expectedLogsPath));
    }

    [Fact]
    public async Task LogInfo_And_LogError_WriteEntriesToFile()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        var utcNow = DateTime.UtcNow;
        service.LogInfo("TestCategory", "Info message", "Info details");
        service.LogError("ErrorCategory", "Error message", "Error details");

        // Retry loop to wait for fire-and-forget background write tasks
        List<SystemLogEntry> entries = new();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            entries = await service.GetLogsAsync(DateTime.UtcNow);
            if (entries.Count >= 2) break;
        }

        Assert.Equal(2, entries.Count);

        var infoEntry = entries.FirstOrDefault(e => e.Severity == "Info");
        Assert.NotNull(infoEntry);
        Assert.Equal("TestCategory", infoEntry.Category);
        Assert.Equal("Info message", infoEntry.Message);
        Assert.Equal("Info details", infoEntry.Details);
        Assert.True(infoEntry.TimestampUtc > DateTime.MinValue);

        var errorEntry = entries.FirstOrDefault(e => e.Severity == "Error");
        Assert.NotNull(errorEntry);
        Assert.Equal("ErrorCategory", errorEntry.Category);
        Assert.Equal("Error message", errorEntry.Message);
        Assert.Equal("Error details", errorEntry.Details);
        Assert.True(errorEntry.TimestampUtc > DateTime.MinValue);
    }

    [Fact]
    public async Task Log_CustomSeverity_WritesEntryToFile()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        service.Log("Security", "Warning", "Failed login attempt", "IP: 127.0.0.1");

        List<SystemLogEntry> entries = new();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            entries = await service.GetLogsAsync(DateTime.UtcNow);
            if (entries.Any(e => e.Severity == "Warning")) break;
        }

        var warningEntry = Assert.Single(entries);
        Assert.Equal("Security", warningEntry.Category);
        Assert.Equal("Warning", warningEntry.Severity);
        Assert.Equal("Failed login attempt", warningEntry.Message);
        Assert.Equal("IP: 127.0.0.1", warningEntry.Details);
        Assert.True(warningEntry.TimestampUtc > DateTime.MinValue);
    }

    [Fact]
    public async Task GetLogsAsync_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        var logs = await service.GetLogsAsync(DateTime.UtcNow.AddDays(-100));

        Assert.NotNull(logs);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task GetLogsAsync_WithCorruptedLines_SkipsCorruptedLinesAndReturnsValidEntries()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        var targetDate = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var logsDir = Path.Combine(_testDir, "Logs");
        Directory.CreateDirectory(logsDir);

        var validEntry = new SystemLogEntry
        {
            TimestampUtc = targetDate,
            Category = "System",
            Severity = "Info",
            Message = "Valid message",
            Details = "Valid details"
        };
        var validJson = JsonSerializer.Serialize(validEntry, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        string[] fileLines =
        [
            validJson,
            "{ not-json }",
            "",
            "   "
        ];

        var filePath = Path.Combine(logsDir, $"log_{targetDate:yyyy-MM-dd}.jsonl");
        await File.WriteAllLinesAsync(filePath, fileLines);

        var results = await service.GetLogsAsync(targetDate);

        Assert.NotNull(results);
        var singleEntry = Assert.Single(results);
        Assert.Equal("System", singleEntry.Category);
        Assert.Equal("Info", singleEntry.Severity);
        Assert.Equal("Valid message", singleEntry.Message);
        Assert.Equal("Valid details", singleEntry.Details);
    }

    [Fact]
    public void GetAvailableLogDates_ReturnsDatesInDescendingOrder()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        var logsDir = Path.Combine(_testDir, "Logs");
        Directory.CreateDirectory(logsDir);

        File.WriteAllText(Path.Combine(logsDir, "log_2026-08-01.jsonl"), "{}");
        File.WriteAllText(Path.Combine(logsDir, "log_2026-08-15.jsonl"), "{}");
        File.WriteAllText(Path.Combine(logsDir, "log_2026-08-10.jsonl"), "{}");
        File.WriteAllText(Path.Combine(logsDir, "invalid_file.txt"), "some content");
        File.WriteAllText(Path.Combine(logsDir, "log_invalid-date.jsonl"), "some content");

        var dates = service.GetAvailableLogDates();

        Assert.NotNull(dates);
        Assert.Equal(3, dates.Count);
        Assert.Equal(new DateTime(2026, 8, 15), dates[0]);
        Assert.Equal(new DateTime(2026, 8, 10), dates[1]);
        Assert.Equal(new DateTime(2026, 8, 1), dates[2]);
    }

    [Fact]
    public void GetAvailableLogDates_WhenDirectoryDoesNotExist_ReturnsEmptyList()
    {
        var nonExistentPath = Path.Combine(_testDir, "NonExistentPath");
        var config = CreateConfig(nonExistentPath);

        var service = new SystemLogService(config);

        // Delete directory created in constructor
        var logsDir = Path.Combine(nonExistentPath, "Logs");
        if (Directory.Exists(logsDir))
        {
            Directory.Delete(logsDir, recursive: true);
        }

        var dates = service.GetAvailableLogDates();

        Assert.NotNull(dates);
        Assert.Empty(dates);
    }
}
