using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models
{
    public class EmployeeTests
    {
        [Fact]
        public void Employee_Initialization_SetsDefaultsCorrectly()
        {
            var emp = new Employee();

            Assert.False(string.IsNullOrEmpty(emp.Id));
            Assert.Equal(string.Empty, emp.FirstName);
            Assert.Equal(string.Empty, emp.LastName);
            Assert.Equal(string.Empty, emp.FullName); // Test derived property

            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), emp.DateOfJoining);
            Assert.Null(emp.EmploymentEndDate);
            Assert.Equal(string.Empty, emp.Email);
            Assert.Equal(string.Empty, emp.Position);

            Assert.Equal(string.Empty, emp.TeamId);
            Assert.Equal(40.0m, emp.WeeklyHours);
            Assert.Equal(0, emp.HourlyRate);

            Assert.Equal(string.Empty, emp.PhoneNumber);
            Assert.Equal(string.Empty, emp.Address);
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), emp.DateOfBirth);
            Assert.Equal(string.Empty, emp.EmergencyContact);
            Assert.Equal(string.Empty, emp.EmergencyContactPhone);
            Assert.Equal(string.Empty, emp.PersonalEmail);

            Assert.True(emp.IsDarkMode);
            Assert.True(emp.PushNotificationsEnabled);
            Assert.True(emp.ChatNotificationsEnabled);
            Assert.True(emp.EmailNotificationsEnabled);
            Assert.Null(emp.AvatarBase64);
            Assert.Null(emp.ProfileColor);

            Assert.False(emp.IsAdmin);
            Assert.True(emp.IsActive);

            Assert.Equal(string.Empty, emp.EncryptedEmailPassword);
            Assert.Equal(string.Empty, emp.SignatureHtml);

            Assert.False(emp.IsEmailPublic);
            Assert.False(emp.UsesCustomSidebarOrder);
            Assert.NotNull(emp.CustomCategoryOrder);
            Assert.Empty(emp.CustomCategoryOrder);
            Assert.NotNull(emp.CustomChannelOrder);
            Assert.Empty(emp.CustomChannelOrder);

            Assert.NotNull(emp.CalendarCategories);
            Assert.Empty(emp.CalendarCategories);

            Assert.Empty(emp.Permissions);
        }

        [Fact]
        public void FullName_ConcatenatesProperly()
        {
            var emp = new Employee { FirstName = "Jane", LastName = "Doe" };
            Assert.Equal("Jane Doe", emp.FullName);

            var emp2 = new Employee { FirstName = "John" };
            Assert.Equal("John", emp2.FullName);

            var emp3 = new Employee { LastName = "Smith" };
            Assert.Equal("Smith", emp3.FullName);
        }

        [Fact]
        public void Initials_GeneratesProperInitials()
        {
            var emp = new Employee { FirstName = "Jane", LastName = "Doe" };
            Assert.Equal("JD", emp.Initials);

            var emp2 = new Employee { FirstName = "john", LastName = "smith" };
            Assert.Equal("JS", emp2.Initials);

            var emp3 = new Employee { FirstName = "Alice" };
            Assert.Equal("A", emp3.Initials);

            var emp4 = new Employee { LastName = "Cooper" };
            Assert.Equal("C", emp4.Initials);

            var emp5 = new Employee();
            Assert.Equal("", emp5.Initials);
        }

        [Fact]
        public void HasPermission_Admin_ReturnsTrueForAnyPermission()
        {
            var admin = new Employee { IsAdmin = true, IsActive = true };

            Assert.True(admin.HasPermission("Anything"));
            Assert.True(admin.HasPermission("Random"));
        }

        [Fact]
        public void HasPermission_NonAdminWithPermission_ReturnsTrue()
        {
            var user = new Employee { IsAdmin = false, IsActive = true };
            user.Permissions.Add("CanEdit");

            Assert.True(user.HasPermission("CanEdit"));
        }

        [Fact]
        public void HasPermission_NonAdminWithoutPermission_ReturnsFalse()
        {
            var user = new Employee { IsAdmin = false, IsActive = true };
            user.Permissions.Add("CanView");

            Assert.False(user.HasPermission("CanEdit"));
        }

        [Fact]
        public void HasPermission_InactiveUser_ReturnsFalseEvenIfAdmin()
        {
            var inactiveAdmin = new Employee { IsAdmin = true, IsActive = false };
            Assert.False(inactiveAdmin.HasPermission("Anything"));

            var inactiveUser = new Employee { IsAdmin = false, IsActive = false };
            inactiveUser.Permissions.Add("CanView");
            Assert.False(inactiveUser.HasPermission("CanView"));
        }

        [Fact]
        public void HasPermission_WithPermissionGroupIdButNoGroupLoaded_ReturnsFalse()
        {
            var user = new Employee { IsAdmin = false, IsActive = true, PermissionGroupId = "some-group-id" };
            user.Permissions.Add("CanEdit"); // It should ignore this and return false

            Assert.False(user.HasPermission("CanEdit"));
        }

        [Fact]
        public void HasPermission_WithPermissionGroupLoaded_ChecksGroupPermissions()
        {
            var user = new Employee { IsAdmin = false, IsActive = true, PermissionGroupId = "some-group-id" };
            user.PermissionGroup = new PermissionGroup { Id = "some-group-id", Permissions = new List<string> { "CanEdit" } };

            Assert.True(user.HasPermission("CanEdit"));
            Assert.False(user.HasPermission("CanView"));
        }

        [Fact]
        public void HasPermission_WhenIsSuspended_ReturnsFalseEvenIfAdmin()
        {
            var admin = new Employee { IsAdmin = true, IsActive = true, IsSuspended = true };
            Assert.False(admin.HasPermission("AnyPermission"));

            var user = new Employee { IsAdmin = false, IsActive = true, IsSuspended = true };
            user.Permissions.Add("CanEdit");
            Assert.False(user.HasPermission("CanEdit"));
        }

        [Fact]
        public void HasPermission_WhenIsBanned_ReturnsFalseEvenIfAdmin()
        {
            var admin = new Employee { IsAdmin = true, IsActive = true, IsBanned = true };
            Assert.False(admin.HasPermission("AnyPermission"));

            var user = new Employee { IsAdmin = false, IsActive = true, IsBanned = true };
            user.Permissions.Add("CanEdit");
            Assert.False(user.HasPermission("CanEdit"));
        }

        [Fact]
        public void DisplaySubtitle_WhenPositionIsNotEmpty_ReturnsPosition()
        {
            var emp = new Employee { Position = "Lead Developer", IsEmailPublic = true, Email = "lead@example.com" };
            Assert.Equal("Lead Developer", emp.DisplaySubtitle);
        }

        [Fact]
        public void DisplaySubtitle_WhenPositionIsEmptyAndIsEmailPublicIsTrue_ReturnsEmail()
        {
            var emp = new Employee { Position = string.Empty, IsEmailPublic = true, Email = "lead@example.com" };
            Assert.Equal("lead@example.com", emp.DisplaySubtitle);
        }

        [Fact]
        public void DisplaySubtitle_WhenPositionIsEmptyAndIsEmailPublicIsFalse_ReturnsEmptyString()
        {
            var emp = new Employee { Position = string.Empty, IsEmailPublic = false, Email = "lead@example.com" };
            Assert.Equal(string.Empty, emp.DisplaySubtitle);
        }

        [Fact]
        public void HasCustomAvatar_WhenAvatarFileIsNonEmpty_ReturnsTrue()
        {
            var emp = new Employee { AvatarFile = "avatar.png", AvatarBase64 = null };
            Assert.True(emp.HasCustomAvatar);
        }

        [Fact]
        public void HasCustomAvatar_WhenAvatarBase64IsNonEmpty_ReturnsTrue()
        {
            var emp = new Employee { AvatarFile = null, AvatarBase64 = "base64-encoded-image-data" };
            Assert.True(emp.HasCustomAvatar);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("", null)]
        [InlineData(null, "")]
        public void HasCustomAvatar_WhenBothAreEmptyOrNull_ReturnsFalse(string? avatarFile, string? avatarBase64)
        {
            var emp = new Employee { AvatarFile = avatarFile, AvatarBase64 = avatarBase64 };
            Assert.False(emp.HasCustomAvatar);
        }

        [Fact]
        public void HasChatPassword_WhenEncryptedPrivateKeyIsNonEmpty_ReturnsTrue()
        {
            var emp = new Employee { EncryptedPrivateKey = "encrypted-chat-key" };
            Assert.True(emp.HasChatPassword);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void HasChatPassword_WhenEncryptedPrivateKeyIsEmptyOrNull_ReturnsFalse(string? key)
        {
            var emp = new Employee { EncryptedPrivateKey = key };
            Assert.False(emp.HasChatPassword);
        }

        [Fact]
        public void NotificationSchedule_DefaultConfiguration_HasSevenDaysWithExpectedDefaults()
        {
            var emp = new Employee();

            Assert.False(emp.NotificationScheduleEnabled);
            Assert.NotNull(emp.NotificationSchedule);
            Assert.Equal(7, emp.NotificationSchedule.Count);

            for (int i = 0; i < 7; i++)
            {
                var schedule = emp.NotificationSchedule[i];
                Assert.Equal((DayOfWeek)i, schedule.Day);
                Assert.True(schedule.IsEnabled);
                Assert.Equal(8, schedule.StartHour);
                Assert.Equal(17, schedule.EndHour);
            }
        }

        [Fact]
        public void Equals_And_GetHashCode_ReferenceEquality_ReturnsTrueAndMatchesHashCode()
        {
            var emp = new Employee { Id = "emp-ref" };
            Assert.True(emp.Equals(emp));
            Assert.Equal(emp.GetHashCode(), emp.GetHashCode());
        }

        [Fact]
        public void Equals_And_GetHashCode_SameId_ReturnsTrueAndMatchesHashCode()
        {
            var emp1 = new Employee { Id = "emp-same" };
            var emp2 = new Employee { Id = "emp-same" };

            Assert.True(emp1.Equals(emp2));
            Assert.True(emp2.Equals(emp1));
            Assert.Equal(emp1.GetHashCode(), emp2.GetHashCode());
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var emp1 = new Employee { Id = "emp-1" };
            var emp2 = new Employee { Id = "emp-2" };

            Assert.False(emp1.Equals(emp2));
            Assert.False(emp2.Equals(emp1));
        }

        [Fact]
        public void Equals_NullOrDifferentType_ReturnsFalse()
        {
            var emp = new Employee { Id = "emp-1" };

            Assert.False(emp.Equals(null));
            Assert.False(emp.Equals("string-object"));
            Assert.False(emp.Equals(new object()));
        }

        [Fact]
        public void Equals_And_GetHashCode_NullIdFallback_DoesNotThrow()
        {
            var emp1 = new Employee { Id = null! };
            var emp2 = new Employee { Id = null! };
            var emp3 = new Employee { Id = "emp-with-id" };

            var hash1 = emp1.GetHashCode();
            var hash2 = emp2.GetHashCode();
            Assert.NotEqual(0, hash1);

            Assert.True(emp1.Equals(emp2));
            Assert.False(emp1.Equals(emp3));
        }

        [Fact]
        public void EnsureCalendarCategories_WhenEmptyAndNoServerProfile_PopulatesSystemDefaults()
        {
            var emp = new Employee();
            Assert.Empty(emp.CalendarCategories);

            var categories = emp.EnsureCalendarCategories(null);

            Assert.Equal(5, categories.Count);
            Assert.Same(emp.CalendarCategories, categories);
            Assert.Contains(categories, c => c.Name == "Work");
            Assert.Contains(categories, c => c.Name == "Meeting");
            Assert.Contains(categories, c => c.Name == "Holiday");
            Assert.Contains(categories, c => c.Name == "Personal");
            Assert.Contains(categories, c => c.Name == "Important");
        }

        [Fact]
        public void EnsureCalendarCategories_WhenEmptyAndServerProfileProvided_PopulatesServerProfileCategories()
        {
            var emp = new Employee();
            var profile = new CompanyProfile
            {
                CalendarCategories =
                [
                    new CalendarCategory { Id = "s-1", Name = "Client Call", Color = "Primary" },
                    new CalendarCategory { Id = "s-2", Name = "Workshop", Color = "Tertiary" }
                ]
            };

            var categories = emp.EnsureCalendarCategories(profile);

            Assert.Equal(2, categories.Count);
            Assert.Contains(categories, c => c.Name == "Client Call" && c.Color == "Primary");
            Assert.Contains(categories, c => c.Name == "Workshop" && c.Color == "Tertiary");
        }

        [Fact]
        public void EnsureCalendarCategories_WhenAlreadyHasCategories_PreservesExistingCategories()
        {
            var emp = new Employee
            {
                CalendarCategories =
                [
                    new CalendarCategory { Id = "custom-1", Name = "My Special Category", Color = "Warning" }
                ]
            };

            var profile = new CompanyProfile();

            var categories = emp.EnsureCalendarCategories(profile);

            Assert.Single(categories);
            Assert.Equal("My Special Category", categories[0].Name);
            Assert.Equal("Warning", categories[0].Color);
        }
    }
}
