namespace Spokes_Server.Aggregate.Contexts;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Core;

/// <summary>
/// Domain aggregate context managing project workspaces, estimation (quotes, rate cards, work types),
/// task boards (Kanban boards, cards, templates), project notes, and documentation.
/// </summary>
public class ProjectsContext
{
    private readonly CompanyProfileRepository? _companyProfile;

    public virtual ProjectRepository Projects { get; }
    public virtual ProjectGroupRepository ProjectGroups { get; }
    public virtual WorkTypeRepository WorkTypes { get; }
    public virtual RateCardRepository RateCards { get; }
    public virtual QuoteRepository Quotes { get; }
    public virtual ProjectNoteRepository ProjectNotes { get; }
    public virtual BoardRepository Boards { get; }
    public virtual BoardCardRepository BoardCards { get; }
    public virtual BoardTemplateRepository BoardTemplates { get; }
    public virtual ProjectDocumentRepository ProjectDocuments { get; }

    protected ProjectsContext()
    {
        Projects = null!;
        ProjectGroups = null!;
        WorkTypes = null!;
        RateCards = null!;
        Quotes = null!;
        ProjectNotes = null!;
        Boards = null!;
        BoardCards = null!;
        BoardTemplates = null!;
        ProjectDocuments = null!;
    }

    [ActivatorUtilitiesConstructor]
    public ProjectsContext(
        ProjectRepository projects,
        ProjectGroupRepository projectGroups,
        WorkTypeRepository workTypes,
        RateCardRepository rateCards,
        QuoteRepository quotes,
        ProjectNoteRepository projectNotes,
        BoardRepository boards,
        BoardCardRepository boardCards,
        BoardTemplateRepository boardTemplates,
        ProjectDocumentRepository projectDocuments,
        CompanyProfileRepository? companyProfile = null)
    {
        Projects = projects;
        ProjectGroups = projectGroups;
        WorkTypes = workTypes;
        RateCards = rateCards;
        Quotes = quotes;
        ProjectNotes = projectNotes;
        Boards = boards;
        BoardCards = boardCards;
        BoardTemplates = boardTemplates;
        ProjectDocuments = projectDocuments;
        _companyProfile = companyProfile;
    }

    /// <summary>
    /// Exposes domain-owned document numbering configuration decomposed from the global company profile.
    /// </summary>
    public virtual DocumentNumberingConfig GetDocumentNumbering() => _companyProfile?.GetDocumentNumbering() ?? new DocumentNumberingConfig();
    public virtual DocumentNumberingConfig DocumentNumbering => GetDocumentNumbering();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => Projects.LoadFromDisk(),
            () => ProjectGroups.LoadFromDisk(),
            () => WorkTypes.LoadFromDisk(),
            () => RateCards.LoadFromDisk(),
            () => Quotes.LoadFromDisk(),
            () => ProjectNotes.LoadFromDisk(),
            () => Boards.LoadFromDisk(),
            () => BoardCards.LoadFromDisk(),
            () => BoardTemplates.LoadFromDisk(),
            () => ProjectDocuments.LoadFromDisk()
        );
    }
}
