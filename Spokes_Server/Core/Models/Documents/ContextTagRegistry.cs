using System.Diagnostics.CodeAnalysis;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Models.Documents;

public static class ContextTagRegistry
{
    /// <summary>
    /// Returns all available context tags with descriptions for autocomplete.
    /// </summary>
    public static List<ContextTagDefinition> GetAvailableTags()
    {
        return
        [
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
        ];
    }

    /// <summary>
    /// Resolves all @tags in the body string against a project context.
    /// Unknown tags are left as-is.
    /// </summary>
    [return: NotNullIfNotNull("body")]
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

        List<(string, string)> replacements =
        [
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
            ("@Client.AddressMultiLine", client != null ? FormatAddress(client.BusinessAddressNumber, client.BusinessAddressStreet, client.BusinessAddressCity, client.BusinessAddressState, client.BusinessAddressZip, client.BusinessAddressCountry, multiLine: true) : ""),
            ("@Client.Address", client != null ? FormatAddress(client.BusinessAddressNumber, client.BusinessAddressStreet, client.BusinessAddressCity, client.BusinessAddressState, client.BusinessAddressZip, client.BusinessAddressCountry, multiLine: false) : ""),

            // Company
            ("@Company.Name", profile?.CompanyName ?? ""),
            ("@Company.AddressMultiLine", profile != null ? FormatAddress(null, profile.AddressStreet, profile.AddressCity, profile.AddressState, profile.AddressZip, profile.AddressCountry, multiLine: true) : ""),
            ("@Company.Address", profile != null ? FormatAddress(null, profile.AddressStreet, profile.AddressCity, profile.AddressState, profile.AddressZip, profile.AddressCountry, multiLine: false) : ""),
            ("@Company.Phone", profile?.PhoneNumber ?? ""),
            ("@Company.Website", profile?.Website ?? ""),

            // Project Manager
            ("@ProjectManager.Name", projectManager?.FullName ?? ""),
            ("@ProjectManager.Email", projectManager?.IsEmailPublic == true ? projectManager.Email : ""),
            ("@ProjectManager.Position", projectManager?.Position ?? ""),

            // Date
            ("@Today", now.ToString("MMMM dd, yyyy")),
            ("@Year", now.Year.ToString()),
        ];

        return replacements;
    }

    private static string FormatAddress(
        string? number,
        string? street,
        string? city,
        string? state,
        string? zip,
        string? country,
        bool multiLine)
    {
        List<string> streetParts = [];
        if (!string.IsNullOrWhiteSpace(number)) streetParts.Add(number);
        if (!string.IsNullOrWhiteSpace(street)) streetParts.Add(street);
        var line1 = string.Join(" ", streetParts);

        List<string> cityState = [];
        if (!string.IsNullOrWhiteSpace(city)) cityState.Add(city);
        if (!string.IsNullOrWhiteSpace(state)) cityState.Add(state);
        if (!string.IsNullOrWhiteSpace(zip)) cityState.Add(zip);

        if (multiLine)
        {
            List<string> lines = [];
            if (!string.IsNullOrEmpty(line1)) lines.Add(line1);
            var line2 = string.Join(", ", cityState);
            if (!string.IsNullOrEmpty(line2)) lines.Add(line2);
            if (!string.IsNullOrWhiteSpace(country)) lines.Add(country);
            return string.Join("\n", lines);
        }

        List<string> parts = [];
        if (!string.IsNullOrEmpty(line1)) parts.Add(line1);
        parts.AddRange(cityState);
        if (!string.IsNullOrWhiteSpace(country)) parts.Add(country);
        return string.Join(", ", parts);
    }
}

public class ContextTagDefinition
{
    public string Tag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
