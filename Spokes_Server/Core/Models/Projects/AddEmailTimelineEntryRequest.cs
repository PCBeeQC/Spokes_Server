namespace Spokes_Server.Core.Models.Projects;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.Communication;

/// <summary>
/// Encapsulates all metadata and file paths required to attach an email to a project's timeline note.
/// </summary>
public record AddEmailTimelineEntryRequest
{
    public required string ProjectId { get; init; }
    public required string MimeFilePath { get; init; }
    public required string EmailId { get; init; }
    public required string Title { get; init; }
    public DateTime Date { get; init; } = DateTime.Today;
    public string? UserNotes { get; init; }
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public IEnumerable<string> ToAddresses { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Factory helper to build an <see cref="AddEmailTimelineEntryRequest"/> from an <see cref="EmailMessage"/>.
    /// </summary>
    public static AddEmailTimelineEntryRequest FromEmailMessage(
        string projectId,
        string mimeFilePath,
        EmailMessage email,
        string title,
        DateTime date,
        string? userNotes,
        string createdBy)
    {
        ArgumentNullException.ThrowIfNull(email);

        return new AddEmailTimelineEntryRequest
        {
            ProjectId = projectId,
            MimeFilePath = mimeFilePath,
            EmailId = email.Id,
            Title = title,
            Date = date,
            UserNotes = userNotes,
            FromName = email.FromName,
            FromAddress = email.FromAddress,
            ToAddresses = email.ToAddresses,
            Subject = email.Subject,
            CreatedBy = createdBy
        };
    }
}
