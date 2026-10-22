namespace Spokes_Server.Core.Services.Identity;

/// <summary>
/// Encapsulates parameters for updating Casdoor admin credentials and application branding in SQLite.
/// </summary>
public sealed record CasdoorBrandingMigrationOptions
{
    public required string DbPath { get; init; }
    public required string BuiltInUsername { get; init; }
    public required string HashedBuiltInPassword { get; init; }
    public required string SpokesUsername { get; init; }
    public required string HashedSpokesPassword { get; init; }
    public required string CompanyName { get; init; }
    public required string LogoUrl { get; init; }
    public required string FaviconUrl { get; init; }
    public required string FixedLogoUrl { get; init; }
    public required string FixedFaviconUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
}

/// <summary>
/// Encapsulates parameters for sanitizing Casdoor organization settings, navigation items, and widget items.
/// </summary>
public sealed record CasdoorOrganizationSanitizationOptions
{
    public required string DbPath { get; init; }
    public required string CompanyName { get; init; }
    public required string FixedLogoUrl { get; init; }
    public required string FixedFaviconUrl { get; init; }
    public int MaxRetries { get; init; } = 10;
}
