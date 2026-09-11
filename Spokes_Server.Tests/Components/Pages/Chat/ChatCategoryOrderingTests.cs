using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Components.Pages.Chat
{
    public class ChatCategoryOrderingTests
    {
        private static List<ChatCategory> CreateDefaultCategories()
        {
            return new List<ChatCategory>
            {
                new ChatCategory { Id = "sys_public", Name = "Public", DisplayOrder = 0, IsSystem = true },
                new ChatCategory { Id = "sys_projects", Name = "Projects", DisplayOrder = 1, IsSystem = true },
                new ChatCategory { Id = "sys_teams", Name = "Teams", DisplayOrder = 2, IsSystem = true },
                new ChatCategory { Id = "sys_groups", Name = "Group Chats", DisplayOrder = 3, IsSystem = true },
                new ChatCategory { Id = "sys_direct", Name = "Direct Messages", DisplayOrder = 4, IsSystem = true },
                new ChatCategory { Id = "sys_archive", Name = "Archived", DisplayOrder = 10000, IsSystem = true }
            };
        }

        private static List<ChatCategory> Reposition(List<ChatCategory> allCategories, string catId, string targetCatId, bool placeBefore)
        {
            var nonArchive = allCategories.Where(c => c.Id != "sys_archive").ToList();
            var cat = nonArchive.First(c => c.Id == catId);
            nonArchive.RemoveAll(c => c.Id == catId);

            int targetIdx = nonArchive.FindIndex(c => c.Id == targetCatId);
            Assert.True(targetIdx >= 0, "Target category must exist in list");

            int insertIdx = placeBefore ? targetIdx : targetIdx + 1;
            nonArchive.Insert(insertIdx, cat);

            var archive = allCategories.FirstOrDefault(c => c.Id == "sys_archive");
            if (archive != null)
            {
                nonArchive.Add(archive);
            }

            return nonArchive;
        }

        [Fact]
        public void Reposition_MoveDirectAbovePublic_MovesDirectToFrontInSingleStep()
        {
            // Scenario: Projects, Teams, Groups are empty (hidden).
            // Visible: [sys_public, sys_direct]
            var categories = CreateDefaultCategories();

            // Moving sys_direct up past sys_public (targetCat = sys_public, placeBefore = true)
            var result = Reposition(categories, "sys_direct", "sys_public", placeBefore: true);

            // sys_direct should now be at index 0 (immediately before sys_public)
            Assert.Equal("sys_direct", result[0].Id);
            Assert.Equal("sys_public", result[1].Id);

            // Intermediate hidden categories should preserve their relative order
            Assert.Equal("sys_projects", result[2].Id);
            Assert.Equal("sys_teams", result[3].Id);
            Assert.Equal("sys_groups", result[4].Id);

            // Archive remains pinned at the bottom
            Assert.Equal("sys_archive", result[5].Id);
        }

        [Fact]
        public void Reposition_MovePublicBelowDirect_MovesPublicAfterDirectInSingleStep()
        {
            // Scenario: All categories are at default, user clicks Down on sys_public to move below sys_direct
            var categories = CreateDefaultCategories();

            // Moving sys_public down past sys_direct (targetCat = sys_direct, placeBefore = false)
            var result = Reposition(categories, "sys_public", "sys_direct", placeBefore: false);

            // Hidden categories remain in order before sys_direct
            Assert.Equal("sys_projects", result[0].Id);
            Assert.Equal("sys_teams", result[1].Id);
            Assert.Equal("sys_groups", result[2].Id);
            Assert.Equal("sys_direct", result[3].Id);
            Assert.Equal("sys_public", result[4].Id);
            Assert.Equal("sys_archive", result[5].Id);
        }

        [Fact]
        public void Reposition_PreservesCustomCategories()
        {
            var categories = CreateDefaultCategories();
            categories.Insert(2, new ChatCategory { Id = "custom_marketing", Name = "Marketing", DisplayOrder = 2, IsSystem = false });

            // Moving sys_direct above custom_marketing
            var result = Reposition(categories, "sys_direct", "custom_marketing", placeBefore: true);

            Assert.Equal("sys_public", result[0].Id);
            Assert.Equal("sys_projects", result[1].Id);
            Assert.Equal("sys_direct", result[2].Id);
            Assert.Equal("custom_marketing", result[3].Id);
            Assert.Equal("sys_teams", result[4].Id);
            Assert.Equal("sys_groups", result[5].Id);
            Assert.Equal("sys_archive", result[6].Id);
        }

        [Fact]
        public void PersonalCategoryOrder_SetsSequentialDisplayOrderAndPinsArchive()
        {
            var categories = CreateDefaultCategories();
            var emp = new Employee();

            // Simulate Personal mode custom order persistence
            for (int i = 0; i < categories.Count; i++)
            {
                emp.CustomCategoryOrder[categories[i].Id] = categories[i].Id == "sys_archive" ? 10000 : i;
            }

            Assert.Equal(0, emp.CustomCategoryOrder["sys_public"]);
            Assert.Equal(1, emp.CustomCategoryOrder["sys_projects"]);
            Assert.Equal(2, emp.CustomCategoryOrder["sys_teams"]);
            Assert.Equal(3, emp.CustomCategoryOrder["sys_groups"]);
            Assert.Equal(4, emp.CustomCategoryOrder["sys_direct"]);
            Assert.Equal(10000, emp.CustomCategoryOrder["sys_archive"]);
        }
    }
}
