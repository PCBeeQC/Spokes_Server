namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

public class EmployeeRepositoryEncryptionTests : TestDataTestBase
{
    [Fact]
    public void SaveAndLoad_EncryptsSensitivePiiAndCompensationOnDisk_WhileKeepingIdentityPlaintext()
    {
        var dataPath = Path.Combine(_testDataPath, "emp_enc");
        Directory.CreateDirectory(dataPath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = dataPath,
                ["SPOKES_MASTER_PASSWORD"] = "TestMasterPassword!2026"
            })
            .Build();

        var encryption = new EncryptionService(config);
        var persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        var repo = new EmployeeRepository(persistence, config, NullLogger<EmployeeRepository>.Instance, encryption);

        var emp = new Employee
        {
            Id = "emp-1",
            FirstName = "Alice",
            LastName = "Tremblay",
            Email = "alice@example.com",
            SocialInsuranceNumber = "123456782",
            PhoneNumber = "514-555-0199",
            Address = "100 Rue Principale, Montreal, QC",
            EmergencyContact = "Bob Tremblay",
            EmergencyContactPhone = "514-555-0100",
            PersonalEmail = "alice.personal@example.com",
            DateOfBirth = new DateOnly(1991, 5, 14),
            HourlyRate = 55.25m,
            Payroll = new EmployeePayrollProfile
            {
                AnnualSalary = 115000.00m
            }
        };

        repo.Save(emp);
        persistence.FlushAll();

        var profilePath = Path.Combine(dataPath, "Employees", emp.Id, "profile.json");
        Assert.True(File.Exists(profilePath));

        var rawJson = File.ReadAllText(profilePath);
        // Identity & UI fields stay readable on disk
        Assert.Contains("Alice", rawJson);
        Assert.Contains("Tremblay", rawJson);
        Assert.Contains("alice@example.com", rawJson);

        // Sensitive PII & compensation fields must NOT appear in plaintext on disk
        Assert.DoesNotContain("123456782", rawJson);
        Assert.DoesNotContain("514-555-0199", rawJson);
        Assert.DoesNotContain("100 Rue Principale", rawJson);
        Assert.DoesNotContain("55.25", rawJson);
        Assert.DoesNotContain("115000", rawJson);
        Assert.Contains("v2:", rawJson);

        // Fresh repository instance transparently decrypts on LoadFromDisk
        var repoReloaded = new EmployeeRepository(persistence, config, NullLogger<EmployeeRepository>.Instance, encryption);
        repoReloaded.LoadFromDisk();
        var loaded = repoReloaded.GetById(emp.Id);

        Assert.NotNull(loaded);
        Assert.Equal("123456782", loaded!.SocialInsuranceNumber);
        Assert.Equal("514-555-0199", loaded.PhoneNumber);
        Assert.Equal("100 Rue Principale, Montreal, QC", loaded.Address);
        Assert.Equal("Bob Tremblay", loaded.EmergencyContact);
        Assert.Equal("514-555-0100", loaded.EmergencyContactPhone);
        Assert.Equal("alice.personal@example.com", loaded.PersonalEmail);
        Assert.Equal(new DateOnly(1991, 5, 14), loaded.DateOfBirth);
        Assert.Equal(55.25m, loaded.HourlyRate);
        Assert.Equal(115000.00m, loaded.Payroll.AnnualSalary);
    }
}
