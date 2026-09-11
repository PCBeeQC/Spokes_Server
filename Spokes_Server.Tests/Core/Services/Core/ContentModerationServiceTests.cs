using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class ContentModerationServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;
        private readonly CompanyProfileRepository _companyProfiles;
        private readonly ContentModerationService _service;

        public ContentModerationServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Moderation_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            _companyProfiles = new CompanyProfileRepository(_persistence, _config);
            _service = new ContentModerationService(_companyProfiles);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public async Task EvaluateTextAsync_NullOrWhiteSpace_ReturnsNoViolation()
        {
            // Act
            var resultNull = await _service.EvaluateTextAsync(null);
            var resultEmpty = await _service.EvaluateTextAsync("");
            var resultSpaces = await _service.EvaluateTextAsync("   ");

            // Assert
            Assert.False(resultNull.HasViolation);
            Assert.Null(resultNull.SanitizedText);

            Assert.False(resultEmpty.HasViolation);
            Assert.Equal("", resultEmpty.SanitizedText);

            Assert.False(resultSpaces.HasViolation);
            Assert.Equal("   ", resultSpaces.SanitizedText);
        }

        [Fact]
        public async Task EvaluateTextAsync_FilterDisabled_ReturnsNoViolation()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = false,
                BlockedWords = new List<string> { "badword" }
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("This is badword content.");

            // Assert
            Assert.False(result.HasViolation);
            Assert.Equal("This is badword content.", result.SanitizedText);
        }

        [Fact]
        public async Task EvaluateTextAsync_NoBlockedWords_ReturnsNoViolation()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                BlockedWords = new List<string>()
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("This is badword content.");

            // Assert
            Assert.False(result.HasViolation);
            Assert.Equal("This is badword content.", result.SanitizedText);
        }

        [Fact]
        public async Task EvaluateTextAsync_BlockedWordPresent_SanitizesAndFlags()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = new List<string> { "badword", "secret" }
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("This is a BADWORD or a secret message.");

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal("This is a *** or a *** message.", result.SanitizedText);
            Assert.Equal(TextModerationAction.Sanitize, result.Action);
        }

        [Fact]
        public async Task EvaluateTextAsync_BlockedWordPartiallyMatches_NoViolation()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                BlockedWords = new List<string> { "ass" }
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("Please assemble the team.");

            // Assert
            Assert.False(result.HasViolation);
            Assert.Equal("Please assemble the team.", result.SanitizedText);
        }
    }
}
