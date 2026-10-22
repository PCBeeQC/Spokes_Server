using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Migrations;

public class AvatarMigrationStepTests : TestDataTestBase
{
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Mock<ILogger<AvatarMigrationStep>> _mockLogger;
    private readonly AvatarMigrationStep _step;

    public AvatarMigrationStepTests()
    {
        var configDict = new Dictionary<string, string?>
        {
            { "DataPath", _testDataPath }
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _employeeRepo = new EmployeeRepository(_persistence, _config);

        _mockLogger = new Mock<ILogger<AvatarMigrationStep>>();
        _step = new AvatarMigrationStep(_employeeRepo, _config, _mockLogger.Object);
    }

    public override void Dispose()
    {
        _persistence.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Step_MetadataProperties_AreConfiguredCorrectly()
    {
        Assert.Equal("20260103_AvatarFileCentric", _step.MigrationKey);
        Assert.Equal(30, _step.Order);
        Assert.Equal("Migrates inline Base64 employee avatars to disk files", _step.Description);
    }

    [Fact]
    public async Task ExecuteAsync_ValidBase64WithComma_WritesAvatarFileAndUpdatesEmployee()
    {
        // Arrange
        var rawBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }; // PNG header magic bytes
        var base64 = Convert.ToBase64String(rawBytes);
        var employee = new Employee
        {
            Id = "emp-valid-1",
            FirstName = "Alice",
            LastName = "Smith",
            AvatarVersion = 1,
            AvatarBase64 = $"data:image/png;base64,{base64}"
        };
        _employeeRepo.Save(employee);

        // Act
        await _step.ExecuteAsync(CancellationToken.None);

        // Assert
        var expectedDir = Path.Combine(_testDataPath, "Employees", "emp-valid-1");
        var expectedFile = Path.Combine(expectedDir, "avatar.png");
        Assert.True(File.Exists(expectedFile), $"Expected file {expectedFile} to exist.");
        var writtenBytes = await File.ReadAllBytesAsync(expectedFile);
        Assert.Equal(rawBytes, writtenBytes);

        var updated = _employeeRepo.GetById("emp-valid-1");
        Assert.NotNull(updated);
        Assert.Equal("avatar.png", updated.AvatarFile);
        Assert.Null(updated.AvatarBase64);
        Assert.Equal(2u, updated.AvatarVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("validbase64withoutcomma_iVBORw0KGgoAAAANSUhEUg==")]
    public async Task ExecuteAsync_NoBase64OrNoComma_LeavesEmployeeUntouched(string? avatarBase64)
    {
        // Arrange
        var empId = $"emp-untouched-{Guid.NewGuid():N}";
        var employee = new Employee
        {
            Id = empId,
            FirstName = "Bob",
            LastName = "Jones",
            AvatarFile = "original.png",
            AvatarVersion = 5,
            AvatarBase64 = avatarBase64
        };
        _employeeRepo.Save(employee);

        // Act
        await _step.ExecuteAsync(CancellationToken.None);

        // Assert
        var expectedFile = Path.Combine(_testDataPath, "Employees", empId, "avatar.png");
        Assert.False(File.Exists(expectedFile));

        var updated = _employeeRepo.GetById(empId);
        Assert.NotNull(updated);
        Assert.Equal("original.png", updated.AvatarFile);
        Assert.Equal(avatarBase64, updated.AvatarBase64);
        Assert.Equal(5u, updated.AvatarVersion);
    }

    [Fact]
    public async Task ExecuteAsync_CorruptBase64String_CatchesExceptionAndContinuesMigration()
    {
        // Arrange
        var corruptEmp = new Employee
        {
            Id = "emp-corrupt",
            FirstName = "Charlie",
            LastName = "Brown",
            AvatarVersion = 1,
            AvatarBase64 = "data:image/png;base64,!!!NotValidBase64!!!"
        };
        _employeeRepo.Save(corruptEmp);

        var validBytes = new byte[] { 1, 2, 3, 4 };
        var validEmp = new Employee
        {
            Id = "emp-valid-after-corrupt",
            FirstName = "Dana",
            LastName = "Scully",
            AvatarVersion = 1,
            AvatarBase64 = $"data:image/png;base64,{Convert.ToBase64String(validBytes)}"
        };
        _employeeRepo.Save(validEmp);

        // Act
        await _step.ExecuteAsync(CancellationToken.None);

        // Assert - Corrupt employee is not migrated and remains in original state
        var corruptFile = Path.Combine(_testDataPath, "Employees", "emp-corrupt", "avatar.png");
        Assert.False(File.Exists(corruptFile));

        var updatedCorrupt = _employeeRepo.GetById("emp-corrupt");
        Assert.NotNull(updatedCorrupt);
        Assert.Equal("data:image/png;base64,!!!NotValidBase64!!!", updatedCorrupt.AvatarBase64);
        Assert.Null(updatedCorrupt.AvatarFile);
        Assert.Equal(1u, updatedCorrupt.AvatarVersion);

        // Assert - Valid employee was migrated successfully
        var validFile = Path.Combine(_testDataPath, "Employees", "emp-valid-after-corrupt", "avatar.png");
        Assert.True(File.Exists(validFile));
        var writtenBytes = await File.ReadAllBytesAsync(validFile);
        Assert.Equal(validBytes, writtenBytes);

        var updatedValid = _employeeRepo.GetById("emp-valid-after-corrupt");
        Assert.NotNull(updatedValid);
        Assert.Equal("avatar.png", updatedValid.AvatarFile);
        Assert.Null(updatedValid.AvatarBase64);
        Assert.Equal(2u, updatedValid.AvatarVersion);

        // Assert - Error was logged for corrupt employee
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to migrate avatar for employee emp-corrupt")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationTokenCancelled_AbortsCleanlyWithoutMigrating()
    {
        // Arrange
        var rawBytes = new byte[] { 42, 43, 44 };
        var employee = new Employee
        {
            Id = "emp-cancel",
            FirstName = "Eve",
            LastName = "Moneypenny",
            AvatarVersion = 1,
            AvatarBase64 = $"data:image/png;base64,{Convert.ToBase64String(rawBytes)}"
        };
        _employeeRepo.Save(employee);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel the token

        // Act
        await _step.ExecuteAsync(cts.Token);

        // Assert - Nothing was written or changed
        var expectedFile = Path.Combine(_testDataPath, "Employees", "emp-cancel", "avatar.png");
        Assert.False(File.Exists(expectedFile));

        var updated = _employeeRepo.GetById("emp-cancel");
        Assert.NotNull(updated);
        Assert.NotNull(updated.AvatarBase64);
        Assert.Null(updated.AvatarFile);
        Assert.Equal(1u, updated.AvatarVersion);
    }
}
