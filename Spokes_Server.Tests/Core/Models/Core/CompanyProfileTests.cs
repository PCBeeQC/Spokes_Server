using System.Text;
using System.Text.Json;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models.Core;

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

        Assert.Equal(0.70m, profile.KilometrageRate);
        Assert.Equal(0.14975m, profile.DefaultTaxRate);
        Assert.Equal("$", profile.CurrencySymbol);
        Assert.Equal("yyyy-MM-dd", profile.DateFormat);
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

    [Fact]
    public void CompanyProfile_DomainConfigs_AreSynchronizedWithPassThroughProperties()
    {
        var profile = new CompanyProfile();

        // Mutate through domain config
        profile.Branding.CompanyName = "Domain Spokes";
        profile.Contact.AddressCity = "Quebec City";
        profile.DocumentNumbering.QuotePrefix = "DEV-Q";
        profile.Accounting.CurrencySymbol = "£";
        profile.Chat.MaxChatCacheSizeMb = 800;
        profile.Push.VapidSubject = "mailto:dev@spokes.sh";
        profile.Backup.BackupsEnabled = true;
        profile.Licensing.LicensePayload = "valid-lic";
        profile.Storage.MaxFileUploadSizeBytes = 50000;
        profile.Auth.AutoCreateEmployeeOnFirstLogin = false;

        // Verify top-level accessors
        Assert.Equal("Domain Spokes", profile.CompanyName);
        Assert.Equal("Quebec City", profile.AddressCity);
        Assert.Equal("DEV-Q", profile.QuotePrefix);
        Assert.Equal("£", profile.CurrencySymbol);
        Assert.Equal(800, profile.MaxChatCacheSizeMb);
        Assert.Equal("mailto:dev@spokes.sh", profile.VapidSubject);
        Assert.True(profile.BackupsEnabled);
        Assert.Equal("valid-lic", profile.LicensePayload);
        Assert.Equal(50000, profile.MaxFileUploadSizeBytes);
        Assert.False(profile.AutoCreateEmployeeOnFirstLogin);

        // Mutate through top-level accessors
        profile.CompanyName = "Updated Top";
        profile.AddressCity = "Montreal";
        profile.CurrencySymbol = "$";
        profile.DateFormat = "dd/MM/yyyy";

        // Verify domain config
        Assert.Equal("Updated Top", profile.Branding.CompanyName);
        Assert.Equal("Montreal", profile.Contact.AddressCity);
        Assert.Equal("$", profile.Accounting.CurrencySymbol);
        Assert.Equal("dd/MM/yyyy", profile.Accounting.DateFormat);
    }

    [Fact]
    public void CompanyProfile_DomainConfigs_RoundTripSerializationWorks()
    {
        var original = new CompanyProfile
        {
            CompanyName = "Serialization Test",
            AddressCity = "Ottawa",
            QuotePrefix = "QT"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<CompanyProfile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Serialization Test", deserialized.CompanyName);
        Assert.Equal("Serialization Test", deserialized.Branding.CompanyName);
        Assert.Equal("Ottawa", deserialized.AddressCity);
        Assert.Equal("Ottawa", deserialized.Contact.AddressCity);
        Assert.Equal("QT", deserialized.QuotePrefix);
        Assert.Equal("QT", deserialized.DocumentNumbering.QuotePrefix);
    }
}

