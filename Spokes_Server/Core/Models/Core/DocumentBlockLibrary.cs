using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Models.Core;

public class BlockFieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public string FieldType { get; set; } = "text"; // "text", "toggle", or "signature"
}

public interface IDocumentBlock
{
    string Type { get; }
    string DisplayName { get; }
    string Description { get; }
    List<BlockFieldDefinition> Fields { get; }

    string RenderHtml(DocumentBlockInstance instance, Project? project, CompanyProfile? profile, Employee? pm);
}

public static class DocumentBlockLibrary
{
    private static readonly List<IDocumentBlock> _blocks = new()
    {
        new LetterheadBlock(),
        new SignatureBlock(),
        new LetterSignatureBlock()
    };

    public static IReadOnlyList<IDocumentBlock> GetAll() => _blocks.AsReadOnly();

    public static IDocumentBlock? GetByType(string type) => _blocks.FirstOrDefault(b => b.Type == type);
}

public class SignatureBlock : IDocumentBlock
{
    public string Type => "Signature";
    public string DisplayName => "Signature Block";
    public string Description => "A dual-column signature block for Client and Company.";

    public List<BlockFieldDefinition> Fields => new()
    {
        new BlockFieldDefinition { Key = "IntroText", Label = "Introductory Text", DefaultValue = "By signing below, the parties agree to the terms herein." },

        new BlockFieldDefinition { Key = "ClientLabel", Label = "Client Signature Label", DefaultValue = "Client Signature" },
        new BlockFieldDefinition { Key = "ClientCompany", Label = "Client Company Name", DefaultValue = "@Client.BusinessName" },
        new BlockFieldDefinition { Key = "ClientName", Label = "Client Name", DefaultValue = "@Client.ContactName" },

        new BlockFieldDefinition { Key = "CompanyLabel", Label = "Company Signature Label", DefaultValue = "Authorized Signature (Spokes)" },
        new BlockFieldDefinition { Key = "CompanyName", Label = "Company Name", DefaultValue = "@Company.Name" },
        new BlockFieldDefinition { Key = "CompanyNameRep", Label = "Company Representative", DefaultValue = "@ProjectManager.Name" }
    };

    public string RenderHtml(DocumentBlockInstance instance, Project? project, CompanyProfile? profile, Employee? pm)
    {
        string GetProp(string key) => instance.Properties.TryGetValue(key, out var val) ? val : Fields.First(f => f.Key == key).DefaultValue;

        string introText = ContextTagRegistry.Resolve(GetProp("IntroText"), project, profile, pm);
        string clientLabel = ContextTagRegistry.Resolve(GetProp("ClientLabel"), project, profile, pm);
        string clientCompany = ContextTagRegistry.Resolve(GetProp("ClientCompany"), project, profile, pm);
        string clientName = ContextTagRegistry.Resolve(GetProp("ClientName"), project, profile, pm);

        string companyLabel = ContextTagRegistry.Resolve(GetProp("CompanyLabel"), project, profile, pm);
        string companyName = ContextTagRegistry.Resolve(GetProp("CompanyName"), project, profile, pm);
        string companyRep = ContextTagRegistry.Resolve(GetProp("CompanyNameRep"), project, profile, pm);

        return $@"
<div class=""mt-8 mb-10 pa-6 keep-together"" style=""border: 1px solid #ccc; background-color: #fafafa;"">
    <MudText Typo=""Typo.body2"" Class=""mb-8"">{introText}</MudText>
    <div class=""print-row d-flex"" style=""gap: 40px;"">
        <div class=""print-col-6"" style=""flex: 1;"">
            <div style=""border-bottom: 1px solid #000; height: 40px; margin-bottom: 8px;""></div>
            <MudText Typo=""Typo.caption"" Style=""font-weight: bold;"">{clientLabel}</MudText>
            <MudText Typo=""Typo.caption"" Class=""d-block"">{clientCompany}</MudText>
            <MudText Typo=""Typo.caption"" Class=""d-block"">{clientName}</MudText>
        </div>
        <div class=""print-col-6"" style=""flex: 1;"">
            <div style=""border-bottom: 1px solid #000; height: 40px; margin-bottom: 8px;""></div>
            <MudText Typo=""Typo.caption"" Style=""font-weight: bold;"">{companyLabel}</MudText>
            <MudText Typo=""Typo.caption"" Class=""d-block"">{companyName}</MudText>
            <MudText Typo=""Typo.caption"" Class=""d-block"">{companyRep}</MudText>
        </div>
    </div>
</div>
";
    }
}

public class LetterheadBlock : IDocumentBlock
{
    public string Type => "Letterhead";
    public string DisplayName => "Letterhead";
    public string Description => "Professional company letterhead with logo, address, date, and recipient.";

    public List<BlockFieldDefinition> Fields => new()
    {
        new BlockFieldDefinition { Key = "ShowCompanyAddress", Label = "Show Company Address & Phone", DefaultValue = "true", FieldType = "toggle" },
        new BlockFieldDefinition { Key = "ShowCompanyName", Label = "Show Company Name Text", DefaultValue = "true", FieldType = "toggle" },
        new BlockFieldDefinition { Key = "Date", Label = "Date", DefaultValue = "@Today" },
        new BlockFieldDefinition { Key = "RecipientName", Label = "Recipient Name", DefaultValue = "@Client.ContactName" },
        new BlockFieldDefinition { Key = "RecipientPosition", Label = "Recipient Position", DefaultValue = "@Client.ContactTitle" },
        new BlockFieldDefinition { Key = "RecipientCompany", Label = "Recipient Company", DefaultValue = "@Client.BusinessName" },
        new BlockFieldDefinition { Key = "RecipientAddress", Label = "Recipient Address", DefaultValue = "" },
        new BlockFieldDefinition { Key = "RecipientPhone", Label = "Recipient Phone", DefaultValue = "" },
        new BlockFieldDefinition { Key = "Subject", Label = "Subject Line", DefaultValue = "" },
        new BlockFieldDefinition { Key = "Greeting", Label = "Greeting", DefaultValue = "Dear @Client.ContactPrefix @Client.ContactName," }
    };

    public string RenderHtml(DocumentBlockInstance instance, Project? project, CompanyProfile? profile, Employee? pm)
    {
        string GetProp(string key) => instance.Properties.TryGetValue(key, out var val) ? val : Fields.First(f => f.Key == key).DefaultValue;
        string Resolve(string v) => ContextTagRegistry.Resolve(v, project, profile, pm);

        bool showCompanyAddress = GetProp("ShowCompanyAddress") == "true";
        bool showCompanyName = GetProp("ShowCompanyName") == "true";
        string date = Resolve(GetProp("Date"));
        string recipientName = Resolve(GetProp("RecipientName"));
        string recipientPosition = Resolve(GetProp("RecipientPosition"));
        string recipientCompany = Resolve(GetProp("RecipientCompany"));
        string recipientAddress = Resolve(GetProp("RecipientAddress"));
        string recipientPhone = Resolve(GetProp("RecipientPhone"));
        string subject = Resolve(GetProp("Subject"));
        string greeting = Resolve(GetProp("Greeting"));

        // Company info
        string companyName = profile?.CompanyName ?? "";
        string logoBase64 = profile?.LogoBase64 ?? "";

        string logoHtml = !string.IsNullOrEmpty(logoBase64)
            ? $"<img src=\"{logoBase64}\" alt=\"{companyName}\" style=\"max-height: 70px; max-width: 200px; object-fit: contain; margin-bottom: 8px;\" />"
            : $"<div style=\"width: 56px; height: 56px; border-radius: 8px; background-color: {profile?.PrimaryColor ?? "#7e6fff"}; color: white; display: flex; align-items: center; justify-content: center; font-size: 1.8rem; font-weight: bold; margin-bottom: 8px; -webkit-print-color-adjust: exact; print-color-adjust: exact;\">{(companyName.Length > 0 ? companyName[0].ToString() : "")}</div>";

        // Conditionally build company address details
        string companyDetailsHtml = "";
        if (showCompanyAddress)
        {
            string street = profile?.AddressStreet ?? "";
            string cityLine = $"{profile?.AddressCity}, {profile?.AddressState} {profile?.AddressZip}".Trim();
            string phone = profile?.PhoneNumber ?? "";
            string website = profile?.Website ?? "";
            companyDetailsHtml = $@"
            <div style=""font-size: 0.85rem; color: #666;"">{street}</div>
            <div style=""font-size: 0.85rem; color: #666;"">{cityLine}</div>
            <div style=""font-size: 0.85rem; color: #666;"">{phone}</div>
            <div style=""font-size: 0.85rem; color: #666;"">{website}</div>";
        }

        string companyNameHtml = showCompanyName
            ? $"<div style=\"font-weight: bold; font-size: 1.1rem;\">{companyName}</div>"
            : "";

        // Recipient optional fields
        string positionSuffix = !string.IsNullOrEmpty(recipientPosition)
            ? $", {recipientPosition}" : "";

        // Build address block (company + address + phone grouped together)
        var addressLines = new List<string>();
        if (!string.IsNullOrEmpty(recipientCompany)) addressLines.Add(recipientCompany);
        if (!string.IsNullOrEmpty(recipientAddress))
        {
            foreach (var line in recipientAddress.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                addressLines.Add(line.Trim());
        }
        if (!string.IsNullOrEmpty(recipientPhone)) addressLines.Add(recipientPhone);

        string addressBlockHtml = addressLines.Any()
            ? string.Join("", addressLines.Select(l => $"<div>{l}</div>"))
            : "";

        string subjectHtml = !string.IsNullOrEmpty(subject)
            ? $"<div style=\"margin-top: 16px; margin-bottom: 4px;\"><strong>{subject}</strong></div>" : "";

        return $@"
<div style=""margin-bottom: 32px;"">
    <!-- Company Header -->
    <div style=""display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 2px solid {profile?.PrimaryColor ?? "#7e6fff"}; padding-bottom: 16px; margin-bottom: 24px;"">
        <div>
            {logoHtml}
            {companyNameHtml}
            {companyDetailsHtml}
        </div>
    </div>
    <!-- Recipient and Date -->
    <div style=""display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 24px; font-size: 0.9rem; line-height: 1.6;"">
        <div>
            <div style=""font-weight: 600; font-size: 0.95rem; margin-bottom: 2px;"">{recipientName}{positionSuffix}</div>
            {addressBlockHtml}
        </div>
        <div style=""text-align: right; color: #444;"">
            <div style=""font-size: 1rem; margin-bottom: 4px;"">{date}</div>
        </div>
    </div>
    {subjectHtml}
    <!-- Greeting -->
    <div style=""margin-bottom: 16px;"">{greeting}</div>
</div>
";
    }
}

public class LetterSignatureBlock : IDocumentBlock
{
    public string Type => "LetterSignature";
    public string DisplayName => "Letter Signature";
    public string Description => "A single-person signature block for the bottom of a letter.";

    public List<BlockFieldDefinition> Fields => new()
    {
        new BlockFieldDefinition { Key = "SignOff", Label = "Sign-off", DefaultValue = "Sincerely," },
        new BlockFieldDefinition { Key = "SignatoryName", Label = "Signatory Name", DefaultValue = "@ProjectManager.Name" },
        new BlockFieldDefinition { Key = "SignatoryRole", Label = "Signatory Role", DefaultValue = "@ProjectManager.Position" },
        new BlockFieldDefinition { Key = "SignatureImage", Label = "Signature Image", DefaultValue = "", FieldType = "signature" }
    };

    public string RenderHtml(DocumentBlockInstance instance, Project? project, CompanyProfile? profile, Employee? pm)
    {
        string GetProp(string key) => instance.Properties.TryGetValue(key, out var val) ? val : Fields.First(f => f.Key == key).DefaultValue;

        string signOff = ContextTagRegistry.Resolve(GetProp("SignOff"), project, profile, pm);
        string name = ContextTagRegistry.Resolve(GetProp("SignatoryName"), project, profile, pm);
        string role = ContextTagRegistry.Resolve(GetProp("SignatoryRole"), project, profile, pm);
        string imageBase64 = GetProp("SignatureImage"); // Base64 image is not resolved as a tag

        string imageHtml = !string.IsNullOrEmpty(imageBase64)
            ? $"<img src=\"{imageBase64}\" alt=\"Signature\" style=\"max-height: 80px; max-width: 250px; object-fit: contain; margin-bottom: 8px;\" />"
            : "<div style=\"height: 80px; width: 250px; border-bottom: 1px dashed #ccc; margin-bottom: 8px;\"></div>";

        string roleHtml = !string.IsNullOrEmpty(role)
            ? $"<div style=\"font-size: 0.9rem; color: #555;\">{role}</div>"
            : "";

        string signOffHtml = !string.IsNullOrEmpty(signOff)
            ? $"<div style=\"margin-bottom: 8px;\">{signOff}</div>"
            : "";

        return $@"
<div class=""keep-together"" style=""margin-top: 32px; margin-bottom: 32px;"">
    {signOffHtml}
    {imageHtml}
    <div style=""font-weight: bold; font-size: 1rem; margin-top: 4px;"">{name}</div>
    {roleHtml}
</div>
";
    }
}

