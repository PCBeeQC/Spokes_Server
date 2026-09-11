using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models
{
    public class ContextTagRegistryTests
    {
        private Project CreateTestProject()
        {
            return new Project
            {
                Name = "Robot Arm Assembly",
                ProjectNumber = "PRJ-2401-0001",
                Description = "Automated robot arm for packaging line",
                Status = ProjectStatus.InProduction,
                Client = new ClientInfo
                {
                    BusinessName = "Acme Corporation",
                    ContactPersonPrefix = "Mr.",
                    ContactPersonName = "John Smith",
                    ContactPersonTitle = "VP of Operations",
                    ContactPersonEmail = "john@acme.com",
                    ContactPersonPhone = "555-1234",
                    BusinessAdressNumber = "123",
                    BusinessAdressStreet = "Industrial Blvd",
                    BusinessAdressCity = "Montreal",
                    BusinessAdressState = "QC",
                    BusinessAdressZip = "H2X 1Y4",
                    BusinessAdressCountry = "Canada"
                }
            };
        }

        private CompanyProfile CreateTestProfile()
        {
            return new CompanyProfile
            {
                CompanyName = "Poly Robotics",
                AddressStreet = "456 Tech Ave",
                AddressCity = "Laval",
                AddressState = "QC",
                AddressZip = "H7T 2T9",
                AddressCountry = "Canada",
                PhoneNumber = "514-555-9876",
                Website = "https://polyrobotics.com"
            };
        }

        private Employee CreateTestManager()
        {
            return new Employee
            {
                FirstName = "Alice",
                LastName = "Martin",
                Email = "alice@polyrobotics.com",
                Position = "Senior Project Manager"
            };
        }

        [Fact]
        public void GetAvailableTags_ReturnsAllExpectedTags()
        {
            var tags = ContextTagRegistry.GetAvailableTags();

            Assert.True(tags.Count >= 20);
            Assert.Contains(tags, t => t.Tag == "@Project.Name");
            Assert.Contains(tags, t => t.Tag == "@Client.BusinessName");
            Assert.Contains(tags, t => t.Tag == "@Company.Name");
            Assert.Contains(tags, t => t.Tag == "@ProjectManager.Name");
            Assert.Contains(tags, t => t.Tag == "@Today");
            Assert.Contains(tags, t => t.Tag == "@Year");
        }

        [Fact]
        public void GetAvailableTags_AllHaveCategoryAndDescription()
        {
            var tags = ContextTagRegistry.GetAvailableTags();

            foreach (var tag in tags)
            {
                Assert.False(string.IsNullOrEmpty(tag.Tag), $"Tag has empty Tag property");
                Assert.False(string.IsNullOrEmpty(tag.Description), $"Tag {tag.Tag} has empty Description");
                Assert.False(string.IsNullOrEmpty(tag.Category), $"Tag {tag.Tag} has empty Category");
            }
        }

        [Fact]
        public void Resolve_ReplacesProjectTags()
        {
            var body = "Project: @Project.Name (#@Project.Number)";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("Project: Robot Arm Assembly (#PRJ-2401-0001)", result);
        }

        [Fact]
        public void Resolve_ReplacesClientTags()
        {
            var body = "Dear @Client.ContactPrefix @Client.ContactName of @Client.BusinessName,";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("Dear Mr. John Smith of Acme Corporation,", result);
        }

        [Fact]
        public void Resolve_ReplacesCompanyTags()
        {
            var body = "From @Company.Name, @Company.Phone, @Company.Website";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("From Poly Robotics, 514-555-9876, https://polyrobotics.com", result);
        }

        [Fact]
        public void Resolve_ReplacesProjectManagerTags()
        {
            var body = "Your PM: @ProjectManager.Name (@ProjectManager.Email), @ProjectManager.Position";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("Your PM: Alice Martin (alice@polyrobotics.com), Senior Project Manager", result);
        }

        [Fact]
        public void Resolve_HandlesNullProjectManager()
        {
            var body = "PM: @ProjectManager.Name, @ProjectManager.Email";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), null);

            Assert.Equal("PM: , ", result);
        }

        [Fact]
        public void Resolve_ReplacesDateTags()
        {
            var body = "Dated @Today, Year @Year";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Contains(DateTime.Now.Year.ToString(), result);
            Assert.Contains(DateTime.Now.ToString("MMMM"), result);
        }

        [Fact]
        public void Resolve_LeavesUnknownTagsUntouched()
        {
            var body = "Hello @Unknown.Tag, this is @Also.Unknown";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("Hello @Unknown.Tag, this is @Also.Unknown", result);
        }

        [Fact]
        public void Resolve_HandlesEmptyBody()
        {
            var result = ContextTagRegistry.Resolve("", CreateTestProject(), CreateTestProfile(), CreateTestManager());
            Assert.Equal("", result);
        }

        [Fact]
        public void Resolve_HandlesNullBody()
        {
            var result = ContextTagRegistry.Resolve(null!, CreateTestProject(), CreateTestProfile(), CreateTestManager());
            Assert.Null(result);
        }

        [Fact]
        public void Resolve_IsCaseInsensitive()
        {
            var body = "Hello @client.businessname";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Equal("Hello Acme Corporation", result);
        }

        [Fact]
        public void Resolve_FormatsClientAddress()
        {
            var body = "Address: @Client.Address";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Contains("123", result);
            Assert.Contains("Industrial Blvd", result);
            Assert.Contains("Montreal", result);
            Assert.Contains("QC", result);
            Assert.Contains("Canada", result);
        }

        [Fact]
        public void Resolve_FormatsCompanyAddress()
        {
            var body = "Our office: @Company.Address";
            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Contains("456 Tech Ave", result);
            Assert.Contains("Laval", result);
            Assert.Contains("QC", result);
        }

        [Fact]
        public void Resolve_FullDocumentScenario()
        {
            var body = @"# Non-Disclosure Agreement

This Non-Disclosure Agreement (""NDA"") is entered into as of @Today by and between:

**@Company.Name** (""Disclosing Party"")
@Company.Address

and

**@Client.BusinessName** (""Receiving Party"")
Attention: @Client.ContactPrefix @Client.ContactName, @Client.ContactTitle
@Client.Address

Re: Project @Project.DisplayName

Project Manager: @ProjectManager.Name (@ProjectManager.Email)";

            var result = ContextTagRegistry.Resolve(body, CreateTestProject(), CreateTestProfile(), CreateTestManager());

            Assert.Contains("Poly Robotics", result);
            Assert.Contains("Acme Corporation", result);
            Assert.Contains("Mr. John Smith, VP of Operations", result);
            Assert.Contains("PRJ-2401-0001 - Robot Arm Assembly", result);
            Assert.Contains("Alice Martin", result);
            Assert.DoesNotContain("@Project", result);
            Assert.DoesNotContain("@Client", result);
            Assert.DoesNotContain("@Company", result);
            Assert.DoesNotContain("@ProjectManager", result);
        }
    }
}
