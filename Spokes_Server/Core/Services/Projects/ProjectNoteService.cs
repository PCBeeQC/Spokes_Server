using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    private readonly Spokes_Server.Core.Data.Repositories.HR.TeamRepository? _teams;

    public ProjectNoteService(
        ProjectNoteRepository projectNotes,
        ProjectRepository projects,
        IFileService fileService,
        Spokes_Server.Core.Data.Repositories.HR.TeamRepository? teams = null)
    {
        _projectNotes = projectNotes;
        _projects = projects;
        _fileService = fileService;
        _teams = teams;
    }

    public virtual List<Project> SearchActiveProjects(string? query, Spokes_Server.Core.Models.HR.Employee? actor = null)
    {
        if (actor == null)
        {
            return [];
        }

        var projects = _projects.GetAll()
            .Where(p => p.IsActive && ProjectAccessHelper.CanEditProject(actor, p, _teams))
            .OrderByDescending(p => p.CreatedAt);

        if (string.IsNullOrWhiteSpace(query))
            return projects.Take(20).ToList();

        return projects
            .Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (p.Client?.BusinessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// Attaches an email to a project timeline note using an encapsulated <see cref="AddEmailTimelineEntryRequest"/>.
    /// </summary>
    public virtual async Task<ProjectNote> AddEmailToTimelineAsync(
        AddEmailTimelineEntryRequest request,
        Spokes_Server.Core.Models.HR.Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = _projects.GetById(request.ProjectId)
            ?? throw new DomainException($"Project {request.ProjectId} not found.", "PROJECT_NOT_FOUND");

        if (!ProjectAccessHelper.CanEditProject(actor, project, _teams))
        {
            throw new DomainException("You do not have permission to modify this project.", "ACCESS_DENIED");
        }

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
            CreatedBy = !string.IsNullOrWhiteSpace(actor?.FullName) ? actor.FullName : request.CreatedBy
        };

        _projectNotes.Save(note);
        return note;
    }

}
