using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
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
    }
}


