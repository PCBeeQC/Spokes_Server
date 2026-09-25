namespace Spokes_Server.Tests.Core.Models.Communication;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Communication;

public class ReportedMessageTests
{
        [Fact]
        public void ReportedMessage_Defaults_AreSetCorrectly()
        {
            var before = DateTime.UtcNow;
            var report = new ReportedMessage();
            var after = DateTime.UtcNow;

            Assert.False(string.IsNullOrWhiteSpace(report.Id));
            Assert.True(Guid.TryParse(report.Id, out _));
            Assert.Equal(string.Empty, report.MessageId);
            Assert.Equal(string.Empty, report.ReporterId);
            Assert.Equal(string.Empty, report.ReportedUserId);
            Assert.Equal(string.Empty, report.Reason);
            Assert.True(report.ReportedAt >= before && report.ReportedAt <= after);
            Assert.Equal("Open", report.Status);
            Assert.Equal(string.Empty, report.SnapshotContent);
            Assert.NotNull(report.Attachments);
            Assert.Empty(report.Attachments);
            Assert.Equal(string.Empty, report.ResolutionAction);
            Assert.Equal(string.Empty, report.ResolvedBy);
        }

        [Fact]
        public void ReportedMessage_Properties_CanBeMutated()
        {
            var reportedAt = DateTime.UtcNow.AddMinutes(-30);
            List<ChatAttachment> attachments =
            [
                new() { Id = "att-1", FileName = "proof.png", FilePath = "/files/proof.png" }
            ];

            var report = new ReportedMessage
            {
                Id = "rep-123",
                MessageId = "msg-456",
                ReporterId = "emp-001",
                ReportedUserId = "emp-002",
                Reason = "Inappropriate content",
                ReportedAt = reportedAt,
                Status = "Resolved",
                SnapshotContent = "Offensive message text",
                Attachments = attachments,
                ResolutionAction = "Deleted Message",
                ResolvedBy = "Moderator Admin"
            };

            Assert.Equal("rep-123", report.Id);
            Assert.Equal("msg-456", report.MessageId);
            Assert.Equal("emp-001", report.ReporterId);
            Assert.Equal("emp-002", report.ReportedUserId);
            Assert.Equal("Inappropriate content", report.Reason);
            Assert.Equal(reportedAt, report.ReportedAt);
            Assert.Equal("Resolved", report.Status);
            Assert.Equal("Offensive message text", report.SnapshotContent);
            Assert.Same(attachments, report.Attachments);
            Assert.Single(report.Attachments);
            Assert.Equal("att-1", report.Attachments[0].Id);
            Assert.Equal("Deleted Message", report.ResolutionAction);
            Assert.Equal("Moderator Admin", report.ResolvedBy);
        }

        [Fact]
        public void Equals_SameReference_ReturnsTrue()
        {
            var report = new ReportedMessage();
            Assert.True(report.Equals(report));
        }

        [Fact]
        public void Equals_SameId_ReturnsTrue()
        {
            var id = Guid.NewGuid().ToString();
            var report1 = new ReportedMessage { Id = id, Reason = "Spam" };
            var report2 = new ReportedMessage { Id = id, Reason = "Harassment" };

            Assert.True(report1.Equals(report2));
            Assert.True(report2.Equals(report1));
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var report1 = new ReportedMessage { Id = "id-1" };
            var report2 = new ReportedMessage { Id = "id-2" };

            Assert.False(report1.Equals(report2));
            Assert.False(report2.Equals(report1));
        }

        [Fact]
        public void Equals_Null_ReturnsFalse()
        {
            var report = new ReportedMessage();
            Assert.False(report.Equals(null));
        }

        [Fact]
        public void Equals_DifferentType_ReturnsFalse()
        {
            var report = new ReportedMessage();
            Assert.False(report.Equals("some-string"));
            Assert.False(report.Equals(new object()));
        }

        [Fact]
        public void GetHashCode_SameId_ReturnsSameHashCode()
        {
            var id = "shared-report-id";
            var report1 = new ReportedMessage { Id = id };
            var report2 = new ReportedMessage { Id = id };

            Assert.Equal(report1.GetHashCode(), report2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_NullId_FallsBackToBaseHashCodeWithoutThrowing()
        {
            var report = new ReportedMessage { Id = null! };

            var exception = Record.Exception(() => report.GetHashCode());

            Assert.Null(exception);
        }
    }
