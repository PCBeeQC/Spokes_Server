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
    public void Constructor_WithDefaultDataPath_DoesNotEagerlyCreateDirectory()
    {
        var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var defaultLogsPath = Path.Combine("Data", "Logs");

        var service = new SystemLogService(emptyConfig);
        Assert.NotNull(service);
        // Constructor must not perform disk I/O side effects
        Assert.False(Directory.Exists(defaultLogsPath));
    }

    [Fact]
    public async Task Constructor_WithCustomDataPath_DoesNotEagerlyCreateDirectory_CreatesOnFirstWrite()
    {
        var customPath = Path.Combine(_testDir, "CustomData");
        var config = CreateConfig(customPath);

        var service = new SystemLogService(config);

        Assert.NotNull(service);
        var expectedLogsPath = Path.Combine(customPath, "Logs");
        Assert.False(Directory.Exists(expectedLogsPath));

        service.LogInfo("InitCategory", "Initial log message");
        await service.FlushAsync();

        Assert.True(Directory.Exists(expectedLogsPath));
    }

    [Fact]
    public async Task LogInfo_And_LogError_WriteEntriesToFile()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        await service.LogInfoAsync("TestCategory", "Info message", "Info details");
        await service.LogErrorAsync("ErrorCategory", "Error message", "Error details");

        var entries = await service.GetLogsAsync(DateTime.UtcNow);
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

        await service.LogAsync("Security", "Warning", "Failed login attempt", "IP: 127.0.0.1");

        var entries = await service.GetLogsAsync(DateTime.UtcNow);
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

    [Fact]
    public async Task SynchronousLog_QueuesAndFlushesWithoutOverwritingOrDroppingEntries()
    {
        var config = CreateConfig(_testDir);
        await using var service = new SystemLogService(config);

        // Multiple concurrent synchronous calls
        const int count = 25;
        Parallel.For(0, count, i =>
        {
            service.Log("ConcurrentCategory", "Info", $"Message {i}", $"Detail {i}");
        });

        // Flush all items
        await service.FlushAsync();

        var logs = await service.GetLogsAsync(DateTime.UtcNow);
        Assert.Equal(count, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal("ConcurrentCategory", log.Category);
            Assert.Equal("Info", log.Severity);
            Assert.StartsWith("Message ", log.Message);
        });
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingEntriesBeforeCompletion()
    {
        var config = CreateConfig(_testDir);
        var service = new SystemLogService(config);

        service.LogInfo("ShutdownCategory", "Shutdown message");
        await service.DisposeAsync();

        var verificationService = new SystemLogService(config);
        var logs = await verificationService.GetLogsAsync(DateTime.UtcNow);
        var entry = Assert.Single(logs);
        Assert.Equal("ShutdownCategory", entry.Category);
        Assert.Equal("Shutdown message", entry.Message);
    }
}
