using System.Text;
using System.Text.Json;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models
{
    public class CompanyProfileTests
    {
        [Fact]
        public void CompanyProfile_Initialization_SetsDefaultsCorrectly()
        {
            var profile = new CompanyProfile();

            Assert.Equal("GlobalProfile", profile.Id);
            Assert.Equal("Family", profile.Edition);
            Assert.Equal("My Spokes", profile.CompanyName);
            Assert.Equal(string.Empty, profile.LogoBase64);
            Assert.Equal(string.Empty, profile.IconBase64);
            Assert.Equal(1, profile.LogoVersion);
            Assert.Equal(1, profile.IconVersion);
            Assert.False(profile.HasCustomLogo);
            Assert.False(profile.HasCustomIcon);
            Assert.Equal("#7e6fff", profile.PrimaryColor);
            Assert.Equal("#1E88E5", profile.SecondaryColor);

            Assert.Equal("Q", profile.QuotePrefix);
            Assert.Equal("INV", profile.InvoicePrefix);

            Assert.NotNull(profile.TaskTranslationLanguages);
            Assert.Empty(profile.TaskTranslationLanguages);

            Assert.Equal(string.Empty, profile.AddressStreet);
            Assert.Equal(string.Empty, profile.AddressCity);
            Assert.Equal(string.Empty, profile.AddressZip);
            Assert.Equal(string.Empty, profile.AddressState);
            Assert.Equal("Canada", profile.AddressCountry);
            Assert.Equal(string.Empty, profile.PhoneNumber);
            Assert.Equal(string.Empty, profile.Website);
            Assert.Equal("https://spokes.sh/privacy/", profile.PrivacyPolicyUrl);

            Assert.Equal(5.0m, profile.DefaultInternalCommission);
            Assert.Equal(10.0m, profile.DefaultExternalCommission);

            Assert.Equal(0.55m, profile.KilometrageRate);
            Assert.Equal(0.14975m, profile.DefaultTaxRate);
            Assert.Equal("$", profile.CurrencySymbol);
            Assert.Equal(new[] { "CAD", "USD", "EUR" }, profile.SupportedCurrencies);

            Assert.NotNull(profile.EmailSettings);
            Assert.Equal(string.Empty, profile.TimeZoneId);

            Assert.NotNull(profile.PermissionGroups);
            Assert.Empty(profile.PermissionGroups);
            Assert.Equal(string.Empty, profile.DefaultPermissionGroupId);

            Assert.Equal(string.Empty, profile.VapidPublicKey);
            Assert.Equal(string.Empty, profile.VapidPrivateKey);
            Assert.Equal("mailto:admin@yourcompany.com", profile.VapidSubject);

            Assert.True(profile.AutoCreateEmployeeOnFirstLogin);

            Assert.Equal("Mentions", profile.DefaultPublicChannelSubscription);
            Assert.Equal(string.Empty, profile.PublicChannelMasterPassword);
            Assert.Equal(500, profile.MaxChatCacheSizeMb);
            Assert.Equal(268435456, profile.MaxFileUploadSizeBytes);
            Assert.Equal("Spokes Default Provider", profile.GifProvider);
            Assert.Equal(string.Empty, profile.GifApiKey);

            Assert.False(profile.BackupsEnabled);
            Assert.Equal(5, profile.BackupRetentionCount);
            Assert.Equal("03:00", profile.BackupTimeLocal);
            Assert.False(profile.BackupEmployeeEmails);

            Assert.Equal(string.Empty, profile.LicensePayload);
            Assert.True(profile.SendUsageStatistics);

            Assert.NotNull(profile.Moderation);
            Assert.NotNull(profile.TimesheetConfig);

            // Check default status creation
            Assert.NotEmpty(profile.ProjectStatuses);
            Assert.Equal(8, profile.ProjectStatuses.Count);
            Assert.Contains(profile.ProjectStatuses, s => s.Id == "draft");
            Assert.Contains(profile.ProjectStatuses, s => s.Name == ProjectStatus.Completed);

            // Check default calendar categories
            Assert.NotEmpty(profile.CalendarCategories);
            Assert.Equal(5, profile.CalendarCategories.Count);
            Assert.Contains(profile.CalendarCategories, c => c.Id == "work");
            Assert.Contains(profile.CalendarCategories, c => c.Name == "Meeting");
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==", true)]
        [InlineData("some-base64-logo", true)]
        public void CompanyProfile_HasCustomLogo_ReturnsExpected(string? logo, bool expected)
        {
            var profile = new CompanyProfile { LogoBase64 = logo! };
            Assert.Equal(expected, profile.HasCustomLogo);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==", true)]
        [InlineData("some-base64-icon", true)]
        public void CompanyProfile_HasCustomIcon_ReturnsExpected(string? icon, bool expected)
        {
            var profile = new CompanyProfile { IconBase64 = icon! };
            Assert.Equal(expected, profile.HasCustomIcon);
        }

        [Fact]
        public void CompanyProfile_PropertyMutations_WorkCorrectly()
        {
            var profile = new CompanyProfile
            {
                Id = "CustomId",
                CompanyName = "Acme Corp",
                Edition = "Business",
                LogoBase64 = "logo123",
                IconBase64 = "icon123",
                LogoVersion = 2,
                IconVersion = 3,
                PrimaryColor = "#000000",
                SecondaryColor = "#ffffff",
                QuotePrefix = "QUOTE-",
                InvoicePrefix = "INV-",
                AddressStreet = "123 Main St",
                AddressCity = "Springfield",
                AddressZip = "12345",
                AddressState = "IL",
                AddressCountry = "USA",
                PhoneNumber = "555-1234",
                Website = "https://example.com",
                PrivacyPolicyUrl = "https://example.com/privacy",
                DefaultInternalCommission = 7.5m,
                DefaultExternalCommission = 15.0m,
                KilometrageRate = 0.60m,
                DefaultTaxRate = 0.15m,
                CurrencySymbol = "€",
                TimeZoneId = "Eastern Standard Time",
                DefaultPermissionGroupId = "admin-group",
                VapidPublicKey = "pub-key",
                VapidPrivateKey = "priv-key",
                VapidSubject = "mailto:ops@example.com",
                AutoCreateEmployeeOnFirstLogin = false,
                DefaultPublicChannelSubscription = "All",
                PublicChannelMasterPassword = "secret-pwd",
                MaxChatCacheSizeMb = 1000,
                MaxFileUploadSizeBytes = 1048576,
                GifProvider = "Giphy",
                GifApiKey = "api-key-xyz",
                BackupsEnabled = true,
                BackupRetentionCount = 10,
                BackupTimeLocal = "04:30",
                BackupEmployeeEmails = true,
                LicensePayload = "licensed-key",
                SendUsageStatistics = false
            };

            Assert.Equal("CustomId", profile.Id);
            Assert.Equal("Acme Corp", profile.CompanyName);
            Assert.Equal("Business", profile.Edition);
            Assert.Equal("logo123", profile.LogoBase64);
            Assert.Equal("icon123", profile.IconBase64);
            Assert.Equal(2, profile.LogoVersion);
            Assert.Equal(3, profile.IconVersion);
            Assert.True(profile.HasCustomLogo);
            Assert.True(profile.HasCustomIcon);
            Assert.Equal("#000000", profile.PrimaryColor);
            Assert.Equal("#ffffff", profile.SecondaryColor);
            Assert.Equal("QUOTE-", profile.QuotePrefix);
            Assert.Equal("INV-", profile.InvoicePrefix);
            Assert.Equal("123 Main St", profile.AddressStreet);
            Assert.Equal("Springfield", profile.AddressCity);
            Assert.Equal("12345", profile.AddressZip);
            Assert.Equal("IL", profile.AddressState);
            Assert.Equal("USA", profile.AddressCountry);
            Assert.Equal("555-1234", profile.PhoneNumber);
            Assert.Equal("https://example.com", profile.Website);
            Assert.Equal("https://example.com/privacy", profile.PrivacyPolicyUrl);
            Assert.Equal(7.5m, profile.DefaultInternalCommission);
            Assert.Equal(15.0m, profile.DefaultExternalCommission);
            Assert.Equal(0.60m, profile.KilometrageRate);
            Assert.Equal(0.15m, profile.DefaultTaxRate);
            Assert.Equal("€", profile.CurrencySymbol);
            Assert.Equal("Eastern Standard Time", profile.TimeZoneId);
            Assert.Equal("admin-group", profile.DefaultPermissionGroupId);
            Assert.Equal("pub-key", profile.VapidPublicKey);
            Assert.Equal("priv-key", profile.VapidPrivateKey);
            Assert.Equal("mailto:ops@example.com", profile.VapidSubject);
            Assert.False(profile.AutoCreateEmployeeOnFirstLogin);
            Assert.Equal("All", profile.DefaultPublicChannelSubscription);
            Assert.Equal("secret-pwd", profile.PublicChannelMasterPassword);
            Assert.Equal(1000, profile.MaxChatCacheSizeMb);
            Assert.Equal(1048576, profile.MaxFileUploadSizeBytes);
            Assert.Equal("Giphy", profile.GifProvider);
            Assert.Equal("api-key-xyz", profile.GifApiKey);
            Assert.True(profile.BackupsEnabled);
            Assert.Equal(10, profile.BackupRetentionCount);
            Assert.Equal("04:30", profile.BackupTimeLocal);
            Assert.True(profile.BackupEmployeeEmails);
            Assert.Equal("licensed-key", profile.LicensePayload);
            Assert.False(profile.SendUsageStatistics);
        }

        [Fact]
        public void EmailServerSettings_Initialization_SetsDefaultsCorrectly()
        {
            var settings = new EmailServerSettings();

            Assert.Equal("", settings.ImapHost);
            Assert.Equal(993, settings.ImapPort);
            Assert.True(settings.ImapSsl);

            Assert.Equal("", settings.SmtpHost);
            Assert.Equal(587, settings.SmtpPort);
            Assert.False(settings.SmtpSsl);
        }

        [Fact]
        public void EmailServerSettings_PropertyMutations_WorkCorrectly()
        {
            var settings = new EmailServerSettings
            {
                ImapHost = "imap.example.com",
                ImapPort = 143,
                ImapSsl = false,
                SmtpHost = "smtp.example.com",
                SmtpPort = 465,
                SmtpSsl = true
            };

            Assert.Equal("imap.example.com", settings.ImapHost);
            Assert.Equal(143, settings.ImapPort);
            Assert.False(settings.ImapSsl);
            Assert.Equal("smtp.example.com", settings.SmtpHost);
            Assert.Equal(465, settings.SmtpPort);
            Assert.True(settings.SmtpSsl);
        }

        [Fact]
        public void CalendarCategory_Initialization_SetsDefaultsCorrectly()
        {
            var category = new CalendarCategory();

            Assert.False(string.IsNullOrEmpty(category.Id));
            Assert.Equal(string.Empty, category.Name);
            Assert.Equal("Default", category.Color);
        }

        [Fact]
        public void CalendarCategory_PropertyMutations_WorkCorrectly()
        {
            var category = new CalendarCategory
            {
                Id = "custom-id",
                Name = "Project Deadline",
                Color = "Warning"
            };

            Assert.Equal("custom-id", category.Id);
            Assert.Equal("Project Deadline", category.Name);
            Assert.Equal("Warning", category.Color);
        }

        [Fact]
        public void ModerationSettings_Initialization_SetsDefaultsCorrectly()
        {
            var moderation = new ModerationSettings();

            Assert.False(moderation.EnableTextFilter);
            Assert.Equal(TextModerationAction.Block, moderation.TextAction);
            Assert.NotNull(moderation.BlockedWords);
            Assert.Empty(moderation.BlockedWords);
        }

        [Fact]
        public void ModerationSettings_PropertyMutations_WorkCorrectly()
        {
            var moderation = new ModerationSettings
            {
                EnableTextFilter = true,
                TextAction = TextModerationAction.Sanitize,
                BlockedWords = new List<string> { "badword1", "badword2" }
            };

            Assert.True(moderation.EnableTextFilter);
            Assert.Equal(TextModerationAction.Sanitize, moderation.TextAction);
            Assert.Equal(2, moderation.BlockedWords.Count);
            Assert.Contains("badword1", moderation.BlockedWords);
            Assert.Contains("badword2", moderation.BlockedWords);
        }

        [Fact]
        public void TimesheetSettings_Initialization_SetsDefaultsCorrectly()
        {
            var timesheet = new TimesheetSettings();

            Assert.True(timesheet.EnableWeeklyPlan);
            Assert.False(timesheet.RequireApproval);
            Assert.False(timesheet.EnableTimesheetLocking);
            Assert.Equal(30, timesheet.LockTimesheetsOlderThanDays);
            Assert.True(timesheet.RequireProject);
            Assert.True(timesheet.RequireTask);
            Assert.False(timesheet.RequireNote);
            Assert.False(timesheet.EnableRounding);
            Assert.Equal(15, timesheet.RoundingIntervalMinutes);
            Assert.Equal("Nearest", timesheet.RoundingDirection);
        }

        [Fact]
        public void TimesheetSettings_PropertyMutations_WorkCorrectly()
        {
            var timesheet = new TimesheetSettings
            {
                EnableWeeklyPlan = false,
                RequireApproval = true,
                EnableTimesheetLocking = true,
                LockTimesheetsOlderThanDays = 60,
                RequireProject = false,
                RequireTask = false,
                RequireNote = true,
                EnableRounding = true,
                RoundingIntervalMinutes = 30,
                RoundingDirection = "Up"
            };

            Assert.False(timesheet.EnableWeeklyPlan);
            Assert.True(timesheet.RequireApproval);
            Assert.True(timesheet.EnableTimesheetLocking);
            Assert.Equal(60, timesheet.LockTimesheetsOlderThanDays);
            Assert.False(timesheet.RequireProject);
            Assert.False(timesheet.RequireTask);
            Assert.True(timesheet.RequireNote);
            Assert.True(timesheet.EnableRounding);
            Assert.Equal(30, timesheet.RoundingIntervalMinutes);
            Assert.Equal("Up", timesheet.RoundingDirection);
        }

        [Theory]
        [InlineData("{\"Edition\": 0}", "Family")]
        [InlineData("{\"Edition\": 1}", "Business")]
        [InlineData("{\"Edition\": 2}", "Business")]
        public void LegacyEditionConverter_Deserialize_NumericValues_ResolvesCorrectly(string json, string expectedEdition)
        {
            var profile = JsonSerializer.Deserialize<CompanyProfile>(json);
            Assert.NotNull(profile);
            Assert.Equal(expectedEdition, profile.Edition);
        }

        [Theory]
        [InlineData("{\"Edition\": \"Family\"}", "Family")]
        [InlineData("{\"Edition\": \"Business\"}", "Business")]
        [InlineData("{\"Edition\": \"CustomEdition\"}", "CustomEdition")]
        public void LegacyEditionConverter_Deserialize_StringValues_ResolvesCorrectly(string json, string expectedEdition)
        {
            var profile = JsonSerializer.Deserialize<CompanyProfile>(json);
            Assert.NotNull(profile);
            Assert.Equal(expectedEdition, profile.Edition);
        }

        [Theory]
        [InlineData("{\"Edition\": null}", "Business")]
        [InlineData("{}", "Family")]
        public void LegacyEditionConverter_Deserialize_NullOrMissing_ResolvesCorrectly(string json, string expectedEdition)
        {
            var profile = JsonSerializer.Deserialize<CompanyProfile>(json);
            Assert.NotNull(profile);
            Assert.Equal(expectedEdition, profile.Edition);
        }

        [Fact]
        public void LegacyEditionConverter_Read_FallbackForNonNumberNonString_ReturnsBusiness()
        {
            var converter = new LegacyEditionConverter();
            var jsonBytes = Encoding.UTF8.GetBytes("true");
            var reader = new Utf8JsonReader(jsonBytes);
            reader.Read(); // TokenType is True (neither Number nor String)

            var result = converter.Read(ref reader, typeof(string), new JsonSerializerOptions());
            Assert.Equal("Business", result);
        }

        [Fact]
        public void LegacyEditionConverter_Serialize_WritesStringValueAccurately()
        {
            var profileFamily = new CompanyProfile { Edition = "Family" };
            var jsonFamily = JsonSerializer.Serialize(profileFamily);
            Assert.Contains("\"Edition\":\"Family\"", jsonFamily);

            var profileBusiness = new CompanyProfile { Edition = "Business" };
            var jsonBusiness = JsonSerializer.Serialize(profileBusiness);
            Assert.Contains("\"Edition\":\"Business\"", jsonBusiness);
        }

        [Fact]
        public void LegacyEditionConverter_WriteDirectly_WritesExpectedJson()
        {
            var converter = new LegacyEditionConverter();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                converter.Write(writer, "Family", new JsonSerializerOptions());
            }

            var output = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Equal("\"Family\"", output);
        }
    }
}
