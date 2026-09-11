using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Models.Core;

public static class ContextTagRegistry
{
    /// <summary>
    /// Returns all available context tags with descriptions for autocomplete.
    /// </summary>
    public static List<ContextTagDefinition> GetAvailableTags()
    {
        return new List<ContextTagDefinition>
        {
            // Project
            new() { Tag = "@Project.Name", Description = "Project name", Category = "Project" },
            new() { Tag = "@Project.Number", Description = "Project number", Category = "Project" },
            new() { Tag = "@Project.DisplayName", Description = "Project number + name", Category = "Project" },
            new() { Tag = "@Project.Description", Description = "Project description", Category = "Project" },
            new() { Tag = "@Project.Status", Description = "Current project status", Category = "Project" },

            // Client
            new() { Tag = "@Client.BusinessName", Description = "Client company name", Category = "Client" },
            new() { Tag = "@Client.ContactName", Description = "Client contact person name", Category = "Client" },
            new() { Tag = "@Client.ContactPrefix", Description = "Contact prefix (Mr./Ms.)", Category = "Client" },
            new() { Tag = "@Client.ContactTitle", Description = "Contact job title", Category = "Client" },
            new() { Tag = "@Client.ContactEmail", Description = "Contact email address", Category = "Client" },
            new() { Tag = "@Client.ContactPhone", Description = "Contact phone number", Category = "Client" },
            new() { Tag = "@Client.Address", Description = "Client address (single line, comma-separated)", Category = "Client" },
            new() { Tag = "@Client.AddressMultiLine", Description = "Client address (multi-line)", Category = "Client" },

            // Company (your company)
            new() { Tag = "@Company.Name", Description = "Your company name", Category = "Company" },
            new() { Tag = "@Company.Address", Description = "Company address (single line, comma-separated)", Category = "Company" },
            new() { Tag = "@Company.AddressMultiLine", Description = "Company address (multi-line)", Category = "Company" },
            new() { Tag = "@Company.Phone", Description = "Your company phone", Category = "Company" },
            new() { Tag = "@Company.Website", Description = "Your company website", Category = "Company" },

            // Project Manager
            new() { Tag = "@ProjectManager.Name", Description = "Project manager full name", Category = "Project Manager" },
            new() { Tag = "@ProjectManager.Email", Description = "Project manager email", Category = "Project Manager" },
            new() { Tag = "@ProjectManager.Position", Description = "Project manager position", Category = "Project Manager" },

            // Date
            new() { Tag = "@Today", Description = "Current date", Category = "Date" },
            new() { Tag = "@Year", Description = "Current year", Category = "Date" },
        };
    }

    /// <summary>
    /// Resolves all @tags in the body string against a project context.
    /// Unknown tags are left as-is.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("body")]
    public static string? Resolve(string? body, Project? project, CompanyProfile? profile, Employee? projectManager)
    {
        if (string.IsNullOrEmpty(body)) return body;

        var replacements = BuildReplacements(project, profile, projectManager);

        foreach (var (tag, value) in replacements)
        {
            body = body.Replace(tag, value, StringComparison.OrdinalIgnoreCase);
        }

        return body;
    }

    private static List<(string Tag, string Value)> BuildReplacements(
        Project? project, CompanyProfile? profile, Employee? projectManager)
    {
        var client = project?.Client;
        var now = DateTime.Now;

        var replacements = new List<(string, string)>
        {
            // Project
            ("@Project.Name", project?.Name ?? ""),
            ("@Project.Number", project?.ProjectNumber ?? ""),
            ("@Project.DisplayName", project?.DisplayName ?? ""),
            ("@Project.Description", project?.Description ?? ""),
            ("@Project.Status", project?.Status ?? ""),

            // Client
            ("@Client.BusinessName", client?.BusinessName ?? ""),
            ("@Client.ContactName", client?.ContactPersonName ?? ""),
            ("@Client.ContactPrefix", client?.ContactPersonPrefix ?? ""),
            ("@Client.ContactTitle", client?.ContactPersonTitle ?? ""),
            ("@Client.ContactEmail", client?.ContactPersonEmail ?? ""),
            ("@Client.ContactPhone", client?.ContactPersonPhone ?? ""),
            ("@Client.AddressMultiLine", client != null ? FormatClientAddressMultiLine(client) : ""),
            ("@Client.Address", client != null ? FormatClientAddressInline(client) : ""),

            // Company
            ("@Company.Name", profile?.CompanyName ?? ""),
            ("@Company.AddressMultiLine", profile != null ? FormatCompanyAddressMultiLine(profile) : ""),
            ("@Company.Address", profile != null ? FormatCompanyAddress(profile) : ""),
            ("@Company.Phone", profile?.PhoneNumber ?? ""),
            ("@Company.Website", profile?.Website ?? ""),

            // Project Manager
            ("@ProjectManager.Name", projectManager?.FullName ?? ""),
            ("@ProjectManager.Email", projectManager?.Email ?? ""),
            ("@ProjectManager.Position", projectManager?.Position ?? ""),

            // Date
            ("@Today", now.ToString("MMMM dd, yyyy")),
            ("@Year", now.Year.ToString()),
        };

        return replacements;
    }

    private static string FormatClientAddressMultiLine(ClientInfo client)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(client.BusinessAdressNumber))
            parts.Add(client.BusinessAdressNumber);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressStreet))
            parts.Add(client.BusinessAdressStreet);

        var line1 = string.Join(" ", parts);

        var cityState = new List<string>();
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressCity))
            cityState.Add(client.BusinessAdressCity);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressState))
            cityState.Add(client.BusinessAdressState);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressZip))
            cityState.Add(client.BusinessAdressZip);

        var line2 = string.Join(", ", cityState);

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(line1)) lines.Add(line1);
        if (!string.IsNullOrEmpty(line2)) lines.Add(line2);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressCountry))
            lines.Add(client.BusinessAdressCountry);

        return string.Join("\n", lines);
    }

    private static string FormatCompanyAddress(CompanyProfile profile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.AddressStreet))
            parts.Add(profile.AddressStreet);
        if (!string.IsNullOrWhiteSpace(profile.AddressCity))
            parts.Add(profile.AddressCity);
        if (!string.IsNullOrWhiteSpace(profile.AddressState))
            parts.Add(profile.AddressState);
        if (!string.IsNullOrWhiteSpace(profile.AddressZip))
            parts.Add(profile.AddressZip);
        if (!string.IsNullOrWhiteSpace(profile.AddressCountry))
            parts.Add(profile.AddressCountry);

        return string.Join(", ", parts);
    }

    private static string FormatClientAddressInline(ClientInfo client)
    {
        var parts = new List<string>();

        var streetParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressNumber))
            streetParts.Add(client.BusinessAdressNumber);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressStreet))
            streetParts.Add(client.BusinessAdressStreet);
        var street = string.Join(" ", streetParts);
        if (!string.IsNullOrEmpty(street)) parts.Add(street);

        if (!string.IsNullOrWhiteSpace(client.BusinessAdressCity))
            parts.Add(client.BusinessAdressCity);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressState))
            parts.Add(client.BusinessAdressState);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressZip))
            parts.Add(client.BusinessAdressZip);
        if (!string.IsNullOrWhiteSpace(client.BusinessAdressCountry))
            parts.Add(client.BusinessAdressCountry);

        return string.Join(", ", parts);
    }

    private static string FormatCompanyAddressMultiLine(CompanyProfile profile)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.AddressStreet))
            lines.Add(profile.AddressStreet);

        var cityState = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.AddressCity))
            cityState.Add(profile.AddressCity);
        if (!string.IsNullOrWhiteSpace(profile.AddressState))
            cityState.Add(profile.AddressState);
        if (!string.IsNullOrWhiteSpace(profile.AddressZip))
            cityState.Add(profile.AddressZip);
        var line2 = string.Join(", ", cityState);
        if (!string.IsNullOrEmpty(line2)) lines.Add(line2);

        if (!string.IsNullOrWhiteSpace(profile.AddressCountry))
            lines.Add(profile.AddressCountry);

        return string.Join("\n", lines);
    }
}

public class ContextTagDefinition
{
    public string Tag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
