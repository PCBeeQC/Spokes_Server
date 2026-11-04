using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ModerationReportFilterHelperTests
{
    private static Dictionary<string, Employee> CreateEmployeeDirectory() =>
        new(StringComparer.Ordinal)
        {
            ["emp-alice"] = new Employee
            {
                Id = "emp-alice",
                FirstName = "Alice",
                LastName = "Reporter",
                Email = "alice@secret-domain.example",
                IsActive = true
            },
            ["emp-victor"] = new Employee
            {
                Id = "emp-victor",
                FirstName = "Victor",
                LastName = "Offender",
                Email = "victor@secret-domain.example",
                IsActive = true
            },
            ["emp-bob"] = new Employee
            {
                Id = "emp-bob",
                FirstName = "Bob",
                LastName = "Builder",
                Email = "bob@secret-domain.example",
                IsActive = true
            },
            ["emp-mod"] = new Employee
            {
                Id = "emp-mod",
                FirstName = "Morgan",
                LastName = "Moderator",
                IsActive = true,
                Permissions = [AppPermissions.Chat.Moderator]
            },
            ["system-deleted-user"] = new Employee
            {
                Id = "system-deleted-user",
                FirstName = "Deleted",
                LastName = "User",
                IsActive = false,
                IsSystem = true
            },
            ["emp-blank"] = new Employee
            {
                Id = "emp-blank",
                FirstName = "",
                LastName = "   ",
                IsActive = true
            }
        };

    [Fact]
    public void BuildItems_MemoizesEmployeeLookups_AndOrdersByReportedAtDescending()
    {
        var directory = CreateEmployeeDirectory();
        var lookupCalls = 0;

        Employee? Resolve(string? id)
        {
            lookupCalls++;
            return id != null && directory.TryGetValue(id, out var emp) ? emp : null;
        }

        var reports = Enumerable.Range(1, 6).Select(i => new ReportedMessage
        {
            Id = $"rep-{i}",
            ReporterId = "emp-alice",
            ReportedUserId = "emp-victor",
            Reason = "Spam or Scams",
            SnapshotContent = $"Spam #{i}",
            ReportedAt = new DateTime(2026, 10, 30, 10, i, 0, DateTimeKind.Utc)
        }).ToList();

        var items = ModerationReportFilterHelper.BuildItems(reports, Resolve);

        Assert.Equal(6, items.Count);
        Assert.Equal("rep-6", items[0].Report.Id);
        Assert.Equal("rep-1", items[^1].Report.Id);
        Assert.Equal(2, lookupCalls); // Only 2 distinct IDs resolved across all 6 reports
    }

    [Fact]
    public void Filter_MatchesReporter_And_ReportedUser_DisplayNames_CaseInsensitive_AndTrimmed()
    {
        var directory = CreateEmployeeDirectory();
        var reports = new[]
        {
            new ReportedMessage
            {
                Id = "rep-victor",
                ReporterId = "emp-alice",
                ReportedUserId = "emp-victor",
                Reason = "Spam or Scams",
                SnapshotContent = "Suspicious link"
            },
            new ReportedMessage
            {
                Id = "rep-bob",
                ReporterId = "emp-victor",
                ReportedUserId = "emp-bob",
                Reason = "Harassment or Bullying",
                SnapshotContent = "Mean comment"
            }
        };

        var items = ModerationReportFilterHelper.BuildItems(
            reports,
            id => id != null && directory.TryGetValue(id, out var e) ? e : null);

        // Reporter first name, last name, full name, and padded query
        var aliceMatches = ModerationReportFilterHelper.Filter(items, "  alice  ");
        Assert.Single(aliceMatches);
        Assert.Equal("rep-victor", aliceMatches[0].Report.Id);

        Assert.Single(ModerationReportFilterHelper.Filter(items, "Alice Reporter"));
        Assert.Single(ModerationReportFilterHelper.Filter(items, "REPORTER"));

        // Reported user ("Bob") matches only rep-bob
        var bobMatches = ModerationReportFilterHelper.Filter(items, "Bob");
        Assert.Single(bobMatches);
        Assert.Equal("rep-bob", bobMatches[0].Report.Id);

        // "Victor" appears as reported user on rep-victor AND reporter on rep-bob
        var victorMatches = ModerationReportFilterHelper.Filter(items, "victor");
        Assert.Equal(2, victorMatches.Count);
    }

    [Fact]
    public void Filter_MatchesCanonicalAndLocalizedReason_Status_Resolution_Attachments_AndModerator()
    {
        var directory = CreateEmployeeDirectory();
        var translations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Harassment or Bullying"] = "Harcèlement ou intimidation",
            ["Resolved"] = "Résolu",
            ["Deleted Message"] = "Message supprimé",
            ["content deleted"] = "contenu supprimé"
        };

        var reports = new[]
        {
            new ReportedMessage
            {
                Id = "rep-open-att",
                ReporterId = "emp-alice",
                ReportedUserId = "emp-victor",
                Reason = "Harassment or Bullying",
                SnapshotContent = "Check attachment",
                Status = ReportedMessageStatus.Open,
                Attachments =
                [
                    new ChatAttachment { Id = "att-1", FileName = "screenshot_evidence.png", FilePath = "/media/1.png" }
                ]
            },
            new ReportedMessage
            {
                Id = "rep-resolved",
                ReporterId = "emp-alice",
                ReportedUserId = "emp-bob",
                Reason = "Spam or Scams",
                SnapshotContent = ReportedMessageResolution.DeletedContentPlaceholder,
                Status = ReportedMessageStatus.Resolved,
                ResolutionAction = ReportedMessageResolution.DeletedMessage,
                ResolvedBy = "Morgan Moderator"
            }
        };

        var items = ModerationReportFilterHelper.BuildItems(
            reports,
            id => id != null && directory.TryGetValue(id, out var e) ? e : null,
            unknownUserLabel: "Inconnu",
            localize: key => translations.TryGetValue(key, out var val) ? val : key);

        // Localized reason AND canonical reason both match rep-open-att
        Assert.Equal("rep-open-att", Assert.Single(ModerationReportFilterHelper.Filter(items, "Harcèlement")).Report.Id);
        Assert.Equal("rep-open-att", Assert.Single(ModerationReportFilterHelper.Filter(items, "Bullying")).Report.Id);

        // Attachment filename matches rep-open-att
        Assert.Equal("rep-open-att", Assert.Single(ModerationReportFilterHelper.Filter(items, "screenshot_evidence")).Report.Id);

        // Localized status, localized resolution action, localized deleted placeholder, and ResolvedBy match rep-resolved
        Assert.Equal("rep-resolved", Assert.Single(ModerationReportFilterHelper.Filter(items, "Résolu")).Report.Id);
        Assert.Equal("rep-resolved", Assert.Single(ModerationReportFilterHelper.Filter(items, "Message supprimé")).Report.Id);
        Assert.Equal("rep-resolved", Assert.Single(ModerationReportFilterHelper.Filter(items, "contenu supprimé")).Report.Id);
        Assert.Equal("rep-resolved", Assert.Single(ModerationReportFilterHelper.Filter(items, "Morgan")).Report.Id);
    }

    [Fact]
    public void BuildItems_HandlesNullEmptyMissingAndSystemUsersSafely()
    {
        var directory = CreateEmployeeDirectory();
        var reports = new[]
        {
            new ReportedMessage
            {
                Id = "rep-null-ids",
                ReporterId = null!,
                ReportedUserId = "",
                Reason = "Other",
                SnapshotContent = "Orphaned report"
            },
            new ReportedMessage
            {
                Id = "rep-missing-and-blank",
                ReporterId = "non-existent-id",
                ReportedUserId = "emp-blank",
                Reason = "Other",
                SnapshotContent = "Blank name user"
            },
            new ReportedMessage
            {
                Id = "rep-deleted-system",
                ReporterId = "system-deleted-user",
                ReportedUserId = "system-deleted-user",
                Reason = "Spam or Scams",
                SnapshotContent = "Deleted account"
            },
            new ReportedMessage
            {
                Id = "rep-mod-target",
                ReporterId = "emp-alice",
                ReportedUserId = "emp-mod",
                Reason = "Other",
                SnapshotContent = "Moderator target"
            }
        };

        var items = ModerationReportFilterHelper.BuildItems(
            reports,
            id =>
            {
                Assert.False(string.IsNullOrWhiteSpace(id)); // Never called with null/whitespace
                return directory.TryGetValue(id!, out var emp) ? emp : null;
            },
            unknownUserLabel: "Unknown User",
            sortByReportedAtDescending: false);

        Assert.Equal("Unknown User", items[0].ReporterName);
        Assert.Equal("Unknown User", items[0].ReportedUserName);
        Assert.False(items[0].CanModerateReportedUser);

        Assert.Equal("Unknown User", items[1].ReporterName);
        Assert.Equal("Unknown User", items[1].ReportedUserName);

        Assert.Equal("Deleted User", items[2].ReporterName);
        Assert.Equal("Deleted User", items[2].ReportedUserName);
        Assert.False(items[2].CanModerateReportedUser); // System account cannot be suspended/banned

        Assert.False(items[3].CanModerateReportedUser); // Moderator account cannot be suspended/banned

        // Searching by fallback label ("Unknown User") or "Deleted User" finds those cards
        Assert.Equal(2, ModerationReportFilterHelper.Filter(items, "Unknown User").Count);
        Assert.Single(ModerationReportFilterHelper.Filter(items, "Deleted User"));
    }

    [Fact]
    public void Filter_DoesNotLeakPrivateEmployeeEmailAddresses()
    {
        var directory = CreateEmployeeDirectory();
        var reports = new[]
        {
            new ReportedMessage
            {
                Id = "rep-privacy",
                ReporterId = "emp-alice",
                ReportedUserId = "emp-victor",
                Reason = "Spam or Scams",
                SnapshotContent = "Normal message text"
            }
        };

        var items = ModerationReportFilterHelper.BuildItems(
            reports,
            id => id != null && directory.TryGetValue(id, out var e) ? e : null);

        // Private email domain 'secret-domain.example' is not displayed on cards and must not match
        Assert.Empty(ModerationReportFilterHelper.Filter(items, "secret-domain.example"));
        Assert.Empty(ModerationReportFilterHelper.Filter(items, "alice@secret-domain.example"));
    }
}
