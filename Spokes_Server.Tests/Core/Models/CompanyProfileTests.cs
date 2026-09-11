using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Linq;

namespace Spokes_Server.Tests.Core.Models
{
    public class CompanyProfileTests
    {
        [Fact]
        public void CompanyProfile_Initialization_SetsDefaultsCorrectly()
        {
            var profile = new CompanyProfile();

            Assert.Equal("GlobalProfile", profile.Id);
            Assert.Equal("My Spokes", profile.CompanyName);
            Assert.Equal(string.Empty, profile.LogoBase64);
            Assert.Equal(string.Empty, profile.IconBase64);
            Assert.Equal("#7e6fff", profile.PrimaryColor);
            Assert.Equal("#1E88E5", profile.SecondaryColor);

            Assert.Equal("Q", profile.QuotePrefix);
            Assert.Equal("INV", profile.InvoicePrefix);

            Assert.Equal(string.Empty, profile.AddressStreet);
            Assert.Equal(string.Empty, profile.AddressCity);
            Assert.Equal(string.Empty, profile.AddressZip);
            Assert.Equal(string.Empty, profile.AddressState);
            Assert.Equal("Canada", profile.AddressCountry);
            Assert.Equal(string.Empty, profile.PhoneNumber);
            Assert.Equal(string.Empty, profile.Website);

            Assert.Equal(5.0m, profile.DefaultInternalCommission);
            Assert.Equal(10.0m, profile.DefaultExternalCommission);

            Assert.Equal(0.55m, profile.KilometrageRate);
            Assert.Equal(0.14975m, profile.DefaultTaxRate);
            Assert.Equal("$", profile.CurrencySymbol);

            Assert.NotNull(profile.EmailSettings);
            Assert.Equal(string.Empty, profile.TimeZoneId);

            Assert.Equal(string.Empty, profile.VapidPublicKey);
            Assert.Equal(string.Empty, profile.VapidPrivateKey);
            Assert.Equal("mailto:admin@yourcompany.com", profile.VapidSubject);

            Assert.True(profile.AutoCreateEmployeeOnFirstLogin);

            Assert.False(profile.BackupsEnabled);
            Assert.Equal(5, profile.BackupRetentionCount);
            Assert.Equal("03:00", profile.BackupTimeLocal);
            Assert.False(profile.BackupEmployeeEmails);

            // Check default status creation
            Assert.NotEmpty(profile.ProjectStatuses);
            Assert.Contains(profile.ProjectStatuses, s => s.Id == "draft");
            Assert.Contains(profile.ProjectStatuses, s => s.Name == ProjectStatus.Completed);

            // Check default calendar categories
            Assert.NotEmpty(profile.CalendarCategories);
            Assert.Contains(profile.CalendarCategories, c => c.Id == "work");
            Assert.Contains(profile.CalendarCategories, c => c.Name == "Meeting");
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
        public void CalendarCategory_Initialization_SetsDefaultsCorrectly()
        {
            var category = new CalendarCategory();

            Assert.False(string.IsNullOrEmpty(category.Id));
            Assert.Equal(string.Empty, category.Name);
            Assert.Equal("Default", category.Color);
        }
    }
}


