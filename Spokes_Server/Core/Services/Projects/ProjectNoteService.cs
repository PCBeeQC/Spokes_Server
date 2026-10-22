using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Projects;

/// <summary>
/// Domain service for managing project timeline notes and attachments.
/// </summary>
public class ProjectNoteService
{
    private readonly ProjectNoteRepository _projectNotes;
    private readonly ProjectRepository _projects;
    private readonly IFileService _fileService;
    private readonly ILogger<ProjectNoteService>? _logger;

    public ProjectNoteService(
        ProjectNoteRepository projectNotes,
        ProjectRepository projects,
        IFileService fileService,
        ILogger<ProjectNoteService>? logger = null)
    {
        _projectNotes = projectNotes;
        _projects = projects;
        _fileService = fileService;
        _logger = logger;
    }

    public virtual List<ProjectNote> GetNotesForProject(string projectId) =>
        string.IsNullOrEmpty(projectId) ? [] : _projectNotes.GetByProject(projectId);

    public virtual List<Project> SearchActiveProjects(string? query)
    {
        var projects = _projects.GetAll()
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.CreatedAt);

        if (string.IsNullOrWhiteSpace(query))
            return projects.Take(20).ToList();

        return projects
            .Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (p.Client?.BusinessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(20)
            .ToList();
    }

    public virtual void SaveNote(ProjectNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        _projectNotes.Save(note);
    }

    public virtual void DeleteNote(string noteId)
    {
        if (!string.IsNullOrEmpty(noteId))
        {
            _projectNotes.Delete(noteId);
        }
    }

    /// <summary>
    /// Attaches an email to a project timeline note using an encapsulated <see cref="AddEmailTimelineEntryRequest"/>.
    /// </summary>
    public virtual async Task<ProjectNote> AddEmailToTimelineAsync(AddEmailTimelineEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = _projects.GetById(request.ProjectId)
            ?? throw new DomainException($"Project {request.ProjectId} not found.", "PROJECT_NOT_FOUND");

        if (!File.Exists(request.MimeFilePath))
            throw new FileNotFoundException("Email MIME file not found on disk.", request.MimeFilePath);

        string emlUrl;
        using (var stream = new FileStream(request.MimeFilePath, FileMode.Open, FileAccess.Read))
        {
            emlUrl = await _fileService.UploadStreamAsync("projectnotes", request.ProjectId, stream, $"{request.EmailId}.eml");
        }

        project.NoteCategories ??= [];
        if (!project.NoteCategories.Contains("Email"))
        {
            project.NoteCategories.Add("Email");
            _projects.Save(project);
        }

        var metadataBlock = $"---\n**From:** {request.FromName} <{request.FromAddress}>\n**To:** {string.Join(", ", request.ToAddresses)}\n**Date:** {request.Date:g}\n**Subject:** {request.Subject}\n---";
        var fullContent = string.IsNullOrWhiteSpace(request.UserNotes)
            ? metadataBlock
            : $"{request.UserNotes}\n\n{metadataBlock}";

        var note = new ProjectNote
        {
            ProjectId = request.ProjectId,
            Title = request.Title,
            Date = request.Date,
            Category = "Email",
            Content = fullContent,
            IsEmail = true,
            EmailHtmlPath = emlUrl,
            CreatedBy = request.CreatedBy
        };

        _projectNotes.Save(note);
        return note;
    }

}
