using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

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

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
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
        var resultNull = await _service.EvaluateTextAsync(null!);
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
            const string original = "This is a BADWORD or a secret message.";
            var result = await _service.EvaluateTextAsync(original);

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal(original.Length, result.SanitizedText.Length);
            Assert.StartsWith("This is a ", result.SanitizedText);
            Assert.EndsWith(" message.", result.SanitizedText);
            Assert.Contains(" or a ", result.SanitizedText);

            // BADWORD (len 7, uppercase) replaced with 7 uppercase letters
            var maskedBadword = result.SanitizedText.Substring(10, 7);
            Assert.Equal(7, maskedBadword.Length);
            Assert.All(maskedBadword, c => Assert.True(char.IsUpper(c)));

            // secret (len 6, lowercase) replaced with 6 lowercase letters
            var maskedSecret = result.SanitizedText.Substring(23, 6);
            Assert.Equal(6, maskedSecret.Length);
            Assert.All(maskedSecret, c => Assert.True(char.IsLower(c)));

            Assert.DoesNotContain("badword", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
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

        [Fact]
        public async Task EvaluateTextAsync_WhenModerationSettingsIsNull_ReturnsNoViolation()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = null!;
            _companyProfiles.Save(profile);

            const string text = "This is badword content.";

            // Act
            var result = await _service.EvaluateTextAsync(text);

            // Assert
            Assert.False(result.HasViolation);
            Assert.Equal(text, result.SanitizedText);
            Assert.Equal(TextModerationAction.Block, result.Action);
        }

        [Fact]
        public async Task EvaluateTextAsync_WhenBlockedWordsIsNull_ReturnsNoViolation()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                BlockedWords = null!
            };
            _companyProfiles.Save(profile);

            const string text = "This is badword content.";

            // Act
            var result = await _service.EvaluateTextAsync(text);

            // Assert
            Assert.False(result.HasViolation);
            Assert.Equal(text, result.SanitizedText);
            Assert.Equal(TextModerationAction.Block, result.Action);
        }

        [Fact]
        public async Task EvaluateTextAsync_WhenBlockedWordsContainsWhitespaceEntries_SkipsWhitespaceAndMatchesValidWords()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                BlockedWords = new List<string> { "  ", "", "badword" }
            };
            _companyProfiles.Save(profile);

            // Act
            const string original = "This is badword content.";
            var result = await _service.EvaluateTextAsync(original);

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal(original.Length, result.SanitizedText.Length);
            Assert.StartsWith("This is ", result.SanitizedText);
            Assert.EndsWith(" content.", result.SanitizedText);
            var mask = result.SanitizedText.Substring(8, 7);
            Assert.Equal(7, mask.Length);
            Assert.All(mask, c => Assert.True(char.IsLetter(c)));
            Assert.DoesNotContain("badword", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(TextModerationAction.Block)]
        [InlineData(TextModerationAction.Sanitize)]
        public async Task EvaluateTextAsync_ActionPreserved(TextModerationAction action)
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = action,
                BlockedWords = new List<string> { "badword" }
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("This is badword content.");

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal(action, result.Action);
        }

        [Fact]
        public async Task EvaluateTextAsync_MultipleOccurrencesOfSameWord_SanitizesAll()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = ["badword"]
            };
            _companyProfiles.Save(profile);

            // Act
            const string original = "badword and badword and BADWORD";
            var result = await _service.EvaluateTextAsync(original);

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal(original.Length, result.SanitizedText.Length);
            var parts = result.SanitizedText.Split(" and ");
            Assert.Equal(3, parts.Length);

            Assert.Equal(7, parts[0].Length);
            Assert.All(parts[0], c => Assert.True(char.IsLower(c)));

            Assert.Equal(7, parts[1].Length);
            Assert.All(parts[1], c => Assert.True(char.IsLower(c)));

            Assert.Equal(7, parts[2].Length);
            Assert.All(parts[2], c => Assert.True(char.IsUpper(c)));

            Assert.DoesNotContain("badword", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EvaluateTextAsync_PreservesMatchingLength_ForDifferentWordLengths()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = ["cat", "damn", "unacceptable"]
            };
            _companyProfiles.Save(profile);

            // Act
            const string original = "A cat said damn which is unacceptable.";
            var result = await _service.EvaluateTextAsync(original);

            // Assert
            Assert.True(result.HasViolation);
            Assert.Equal(original.Length, result.SanitizedText.Length);

            // Check "cat" (3 chars) at index 2
            var catMask = result.SanitizedText.Substring(2, 3);
            Assert.Equal(3, catMask.Length);
            Assert.All(catMask, c => Assert.True(char.IsLetter(c)));

            // Check "damn" (4 chars) at index 11
            var damnMask = result.SanitizedText.Substring(11, 4);
            Assert.Equal(4, damnMask.Length);
            Assert.All(damnMask, c => Assert.True(char.IsLetter(c)));

            // Check "unacceptable" (12 chars) at index 25
            var unaccMask = result.SanitizedText.Substring(25, 12);
            Assert.Equal(12, unaccMask.Length);
            Assert.All(unaccMask, c => Assert.True(char.IsLetter(c)));

            Assert.DoesNotContain("cat", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("damn", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unacceptable", result.SanitizedText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EvaluateTextAsync_TitleCase_PreservesTitleCase()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = ["badword"]
            };
            _companyProfiles.Save(profile);

            // Act
            var result = await _service.EvaluateTextAsync("Badword is prohibited.");

            // Assert
            Assert.True(result.HasViolation);
            var mask = result.SanitizedText.Substring(0, 7);
            Assert.Equal(7, mask.Length);
            Assert.True(char.IsUpper(mask[0]));
            Assert.All(mask.Substring(1), c => Assert.True(char.IsLower(c)));
        }

        [Fact]
        public async Task EvaluateTextAsync_LongerWordPrecedence_AvoidsSubpartSplitting()
        {
            // Arrange: "bad" vs "badword"
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = ["bad", "badword"]
            };
            _companyProfiles.Save(profile);

            // Act
            const string original = "badword bad";
            var result = await _service.EvaluateTextAsync(original);

            // Assert: "badword" is masked as full 7-letter word, not 3-letter + "word"
            Assert.True(result.HasViolation);
            Assert.Equal(original.Length, result.SanitizedText.Length);
            var parts = result.SanitizedText.Split(' ');
            Assert.Equal(2, parts.Length);
            Assert.Equal(7, parts[0].Length);
            Assert.All(parts[0], c => Assert.True(char.IsLetter(c)));
            Assert.DoesNotContain("word", parts[0], StringComparison.OrdinalIgnoreCase);

            Assert.Equal(3, parts[1].Length);
            Assert.All(parts[1], c => Assert.True(char.IsLetter(c)));
        }

        [Fact]
        public async Task EvaluateTextAsync_MarkdownIntegration_DoesNotTriggerThematicBreakOrSwallowFormatting()
        {
            // Arrange
            var profile = _companyProfiles.Get();
            profile.Moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = ["badword"]
            };
            _companyProfiles.Save(profile);

            var markdownSanitizer = new MarkdownSanitizerService();

            // Case 1: Standalone word on its own line (previously *** turned into <hr />)
            var resultLine = await _service.EvaluateTextAsync("badword");
            var htmlLine = markdownSanitizer.RenderSanitizedHtml(resultLine.SanitizedText);
            Assert.DoesNotContain("<hr", htmlLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<p>", htmlLine);

            // Case 2: Two blocked words in one sentence with markdown bold
            // (previously *** word *** swallowed the asterisks and turned into bold-italics)
            const string markdownInput = "This **bold text** has badword and badword!";
            var resultMulti = await _service.EvaluateTextAsync(markdownInput);
            var htmlMulti = markdownSanitizer.RenderSanitizedHtml(resultMulti.SanitizedText);

            // Bold markup remains intact as strong
            Assert.Contains("<strong>bold text</strong>", htmlMulti);
            // Neither badword nor swallowed text
            Assert.DoesNotContain("badword", htmlMulti, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<hr", htmlMulti, StringComparison.OrdinalIgnoreCase);
        }
    }
