using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models.Core;

public class DocumentBlockLibraryTests
{
    private static Project CreateTestProject()
    {
        return new Project
        {
            Name = "Alpha Robot Project",
            ProjectNumber = "PRJ-2026-001",
            Client = new ClientInfo
            {
                BusinessName = "Acme Industries",
                ContactPersonPrefix = "Dr.",
                ContactPersonName = "Jane Doe",
                ContactPersonTitle = "Chief Technology Officer",
                ContactPersonEmail = "jane@acme.com",
                ContactPersonPhone = "555-111-2222"
            }
        };
    }

    private static CompanyProfile CreateTestProfile(bool withLogo = true)
    {
        return new CompanyProfile
        {
            CompanyName = "Poly Robotics Inc.",
            AddressStreet = "100 Innovation Way",
            AddressCity = "Montreal",
            AddressState = "QC",
            AddressZip = "H3A 1A1",
            PhoneNumber = "514-555-0100",
            Website = "https://polyrobotics.com",
            PrimaryColor = "#0055ff",
            LogoBase64 = withLogo ? "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" : ""
        };
    }

    private static Employee CreateTestEmployee()
    {
        return new Employee
        {
            FirstName = "John",
            LastName = "Smith",
            Email = "john.smith@polyrobotics.com",
            Position = "Senior Robotics Engineer"
        };
    }

    #region BlockFieldDefinition Tests

    [Fact]
    public void BlockFieldDefinition_DefaultValues_AreEmptyOrText()
    {
        var field = new BlockFieldDefinition();

        Assert.Equal(string.Empty, field.Key);
        Assert.Equal(string.Empty, field.Label);
        Assert.Equal(string.Empty, field.DefaultValue);
        Assert.Equal("text", field.FieldType);
    }

    [Fact]
    public void BlockFieldDefinition_SetProperties_StoresValuesCorrectly()
    {
        var field = new BlockFieldDefinition
        {
            Key = "CustomKey",
            Label = "Custom Label",
            DefaultValue = "Default",
            FieldType = "toggle"
        };

        Assert.Equal("CustomKey", field.Key);
        Assert.Equal("Custom Label", field.Label);
        Assert.Equal("Default", field.DefaultValue);
        Assert.Equal("toggle", field.FieldType);
    }

    #endregion

    #region DocumentBlockLibrary Registry Tests

    [Fact]
    public void GetAll_ReturnsThreeBlocksInExpectedOrder()
    {
        var blocks = DocumentBlockLibrary.GetAll();

        Assert.NotNull(blocks);
        Assert.Equal(3, blocks.Count);
        Assert.IsType<LetterheadBlock>(blocks[0]);
        Assert.IsType<SignatureBlock>(blocks[1]);
        Assert.IsType<LetterSignatureBlock>(blocks[2]);
    }

    [Theory]
    [InlineData("Letterhead", typeof(LetterheadBlock))]
    [InlineData("Signature", typeof(SignatureBlock))]
    [InlineData("LetterSignature", typeof(LetterSignatureBlock))]
    public void GetByType_ValidType_ReturnsCorrectBlockInstance(string typeName, Type expectedType)
    {
        var block = DocumentBlockLibrary.GetByType(typeName);

        Assert.NotNull(block);
        Assert.IsType(expectedType, block);
        Assert.Equal(typeName, block.Type);
    }

    [Theory]
    [InlineData("letterhead")]
    [InlineData("signature")]
    [InlineData("NonExistent")]
    [InlineData("")]
    [InlineData(" ")]
    public void GetByType_InvalidOrUnknownType_ReturnsNull(string typeName)
    {
        var block = DocumentBlockLibrary.GetByType(typeName);

        Assert.Null(block);
    }

    #endregion

    #region SignatureBlock Tests

    [Fact]
    public void SignatureBlock_Metadata_MatchesSpecifications()
    {
        var block = new SignatureBlock();

        Assert.Equal("Signature", block.Type);
        Assert.Equal("Signature Block", block.DisplayName);
        Assert.Equal("A dual-column signature block for Client and Company.", block.Description);
    }

    [Fact]
    public void SignatureBlock_Fields_ConfiguredCorrectly()
    {
        var block = new SignatureBlock();
        var fields = block.Fields;

        Assert.Equal(7, fields.Count);

        Assert.Contains(fields, f => f.Key == "IntroText" && f.Label == "Introductory Text" && f.DefaultValue == "By signing below, the parties agree to the terms herein." && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "ClientLabel" && f.Label == "Client Signature Label" && f.DefaultValue == "Client Signature" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "ClientCompany" && f.Label == "Client Company Name" && f.DefaultValue == "@Client.BusinessName" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "ClientName" && f.Label == "Client Name" && f.DefaultValue == "@Client.ContactName" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "CompanyLabel" && f.Label == "Company Signature Label" && f.DefaultValue == "Authorized Signature (Spokes)" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "CompanyName" && f.Label == "Company Name" && f.DefaultValue == "@Company.Name" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "CompanyNameRep" && f.Label == "Company Representative" && f.DefaultValue == "@ProjectManager.Name" && f.FieldType == "text");
    }

    [Fact]
    public void SignatureBlock_RenderHtml_DefaultProperties_ResolvesContextTags()
    {
        var block = new SignatureBlock();
        var instance = new DocumentBlockInstance { Type = "Signature" };
        var project = CreateTestProject();
        var profile = CreateTestProfile();
        var pm = CreateTestEmployee();

        string html = block.RenderHtml(instance, project, profile, pm);

        Assert.NotNull(html);
        Assert.Contains("By signing below, the parties agree to the terms herein.", html);
        Assert.Contains("Client Signature", html);
        Assert.Contains("Acme Industries", html);
        Assert.Contains("Jane Doe", html);
        Assert.Contains("Authorized Signature (Spokes)", html);
        Assert.Contains("Poly Robotics Inc.", html);
        Assert.Contains("John Smith", html);
    }

    [Fact]
    public void SignatureBlock_RenderHtml_CustomOverriddenProperties_RendersCustomValues()
    {
        var block = new SignatureBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "Signature",
            Properties = new Dictionary<string, string>
            {
                { "IntroText", "Custom acceptance terms and conditions." },
                { "ClientLabel", "Primary Client Approval" },
                { "ClientCompany", "Overridden Client Corp" },
                { "ClientName", "Alice Walker" },
                { "CompanyLabel", "Executive Officer Signoff" },
                { "CompanyName", "Spokes Robotics Global" },
                { "CompanyNameRep", "Robert Vance" }
            }
        };

        var project = CreateTestProject();
        var profile = CreateTestProfile();
        var pm = CreateTestEmployee();

        string html = block.RenderHtml(instance, project, profile, pm);

        Assert.Contains("Custom acceptance terms and conditions.", html);
        Assert.Contains("Primary Client Approval", html);
        Assert.Contains("Overridden Client Corp", html);
        Assert.Contains("Alice Walker", html);
        Assert.Contains("Executive Officer Signoff", html);
        Assert.Contains("Spokes Robotics Global", html);
        Assert.Contains("Robert Vance", html);
    }

    [Fact]
    public void SignatureBlock_RenderHtml_NullContextObjects_RendersGracefully()
    {
        var block = new SignatureBlock();
        var instance = new DocumentBlockInstance { Type = "Signature" };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.NotNull(html);
        Assert.Contains("By signing below, the parties agree to the terms herein.", html);
        Assert.Contains("Client Signature", html);
        Assert.Contains("Authorized Signature (Spokes)", html);
    }

    #endregion

    #region LetterheadBlock Tests

    [Fact]
    public void LetterheadBlock_Metadata_MatchesSpecifications()
    {
        var block = new LetterheadBlock();

        Assert.Equal("Letterhead", block.Type);
        Assert.Equal("Letterhead", block.DisplayName);
        Assert.Equal("Professional company letterhead with logo, address, date, and recipient.", block.Description);
    }

    [Fact]
    public void LetterheadBlock_Fields_ConfiguredCorrectly()
    {
        var block = new LetterheadBlock();
        var fields = block.Fields;

        Assert.Equal(10, fields.Count);

        Assert.Contains(fields, f => f.Key == "ShowCompanyAddress" && f.Label == "Show Company Address & Phone" && f.DefaultValue == "true" && f.FieldType == "toggle");
        Assert.Contains(fields, f => f.Key == "ShowCompanyName" && f.Label == "Show Company Name Text" && f.DefaultValue == "true" && f.FieldType == "toggle");
        Assert.Contains(fields, f => f.Key == "Date" && f.Label == "Date" && f.DefaultValue == "@Today" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "RecipientName" && f.Label == "Recipient Name" && f.DefaultValue == "@Client.ContactName" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "RecipientPosition" && f.Label == "Recipient Position" && f.DefaultValue == "@Client.ContactTitle" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "RecipientCompany" && f.Label == "Recipient Company" && f.DefaultValue == "@Client.BusinessName" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "RecipientAddress" && f.Label == "Recipient Address" && f.DefaultValue == "" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "RecipientPhone" && f.Label == "Recipient Phone" && f.DefaultValue == "" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "Subject" && f.Label == "Subject Line" && f.DefaultValue == "" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "Greeting" && f.Label == "Greeting" && f.DefaultValue == "Dear @Client.ContactPrefix @Client.ContactName," && f.FieldType == "text");
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_WithLogoBase64_RendersImgTag()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance { Type = "Letterhead" };
        var profile = CreateTestProfile(withLogo: true);
        var project = CreateTestProject();

        string html = block.RenderHtml(instance, project, profile, null);

        Assert.Contains("<img src=\"data:image/png;base64,", html);
        Assert.Contains("alt=\"Poly Robotics Inc.\"", html);
        Assert.DoesNotContain("width: 56px; height: 56px;", html);
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_WithEmptyLogoBase64_RendersFallbackInitialLetterBlock()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance { Type = "Letterhead" };
        var profile = CreateTestProfile(withLogo: false);
        var project = CreateTestProject();

        string html = block.RenderHtml(instance, project, profile, null);

        Assert.DoesNotContain("<img", html);
        Assert.Contains("width: 56px; height: 56px; border-radius: 8px;", html);
        Assert.Contains("background-color: #0055ff", html);
        Assert.Contains(">P</div>", html); // First letter of "Poly Robotics Inc."
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_WithEmptyCompanyNameAndNoLogo_RendersEmptyInitial()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance { Type = "Letterhead" };
        var profile = new CompanyProfile
        {
            CompanyName = "",
            LogoBase64 = "",
            PrimaryColor = "#112233"
        };

        string html = block.RenderHtml(instance, null, profile, null);

        Assert.Contains("background-color: #112233", html);
        Assert.Contains("></div>", html);
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_NullProfile_UsesFallbackColor()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance { Type = "Letterhead" };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.Contains("background-color: #7e6fff", html);
        Assert.Contains("border-bottom: 2px solid #7e6fff", html);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void LetterheadBlock_RenderHtml_ShowCompanyAddressToggle(string toggleValue, bool shouldRenderAddress)
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "Letterhead",
            Properties = new Dictionary<string, string>
            {
                { "ShowCompanyAddress", toggleValue }
            }
        };
        var profile = CreateTestProfile();

        string html = block.RenderHtml(instance, null, profile, null);

        if (shouldRenderAddress)
        {
            Assert.Contains("100 Innovation Way", html);
            Assert.Contains("Montreal, QC H3A 1A1", html);
            Assert.Contains("514-555-0100", html);
            Assert.Contains("https://polyrobotics.com", html);
        }
        else
        {
            Assert.DoesNotContain("100 Innovation Way", html);
            Assert.DoesNotContain("Montreal, QC H3A 1A1", html);
            Assert.DoesNotContain("514-555-0100", html);
            Assert.DoesNotContain("https://polyrobotics.com", html);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void LetterheadBlock_RenderHtml_ShowCompanyNameToggle(string toggleValue, bool shouldRenderName)
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "Letterhead",
            Properties = new Dictionary<string, string>
            {
                { "ShowCompanyName", toggleValue }
            }
        };
        var profile = CreateTestProfile();

        string html = block.RenderHtml(instance, null, profile, null);

        if (shouldRenderName)
        {
            Assert.Contains("<div style=\"font-weight: bold; font-size: 1.1rem;\">Poly Robotics Inc.</div>", html);
        }
        else
        {
            Assert.DoesNotContain("<div style=\"font-weight: bold; font-size: 1.1rem;\">", html);
        }
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_RecipientOptionalFields_PositionAddressPhoneSubject()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "Letterhead",
            Properties = new Dictionary<string, string>
            {
                { "RecipientName", "Bruce Wayne" },
                { "RecipientPosition", "CEO" },
                { "RecipientCompany", "Wayne Enterprises" },
                { "RecipientAddress", "1007 Mountain Drive\nGotham City, NJ 07001" },
                { "RecipientPhone", "555-0199" },
                { "Subject", "Notice of Robotics Acquisition" },
                { "Greeting", "Greetings Mr. Wayne," },
                { "Date", "October 31, 2026" }
            }
        };

        string html = block.RenderHtml(instance, null, null, null);

        // Name with position suffix
        Assert.Contains("Bruce Wayne, CEO", html);
        // Address block elements
        Assert.Contains("<div>Wayne Enterprises</div>", html);
        Assert.Contains("<div>1007 Mountain Drive</div>", html);
        Assert.Contains("<div>Gotham City, NJ 07001</div>", html);
        Assert.Contains("<div>555-0199</div>", html);
        // Subject line
        Assert.Contains("<div style=\"margin-top: 16px; margin-bottom: 4px;\"><strong>Notice of Robotics Acquisition</strong></div>", html);
        // Greeting & Date
        Assert.Contains("Greetings Mr. Wayne,", html);
        Assert.Contains("October 31, 2026", html);
    }

    [Fact]
    public void LetterheadBlock_RenderHtml_EmptyOptionalRecipientFields_OmitsEmptyElements()
    {
        var block = new LetterheadBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "Letterhead",
            Properties = new Dictionary<string, string>
            {
                { "RecipientName", "Clark Kent" },
                { "RecipientPosition", "" },
                { "RecipientCompany", "" },
                { "RecipientAddress", "" },
                { "RecipientPhone", "" },
                { "Subject", "" },
                { "Greeting", "Hello Clark," }
            }
        };

        string html = block.RenderHtml(instance, null, null, null);

        // Name without position comma suffix
        Assert.Contains("Clark Kent</div>", html);
        Assert.DoesNotContain("Clark Kent,", html);
        // No subject line
        Assert.DoesNotContain("<strong>", html);
        // Greeting present
        Assert.Contains("Hello Clark,", html);
    }

    #endregion

    #region LetterSignatureBlock Tests

    [Fact]
    public void LetterSignatureBlock_Metadata_MatchesSpecifications()
    {
        var block = new LetterSignatureBlock();

        Assert.Equal("LetterSignature", block.Type);
        Assert.Equal("Letter Signature", block.DisplayName);
        Assert.Equal("A single-person signature block for the bottom of a letter.", block.Description);
    }

    [Fact]
    public void LetterSignatureBlock_Fields_ConfiguredCorrectly()
    {
        var block = new LetterSignatureBlock();
        var fields = block.Fields;

        Assert.Equal(4, fields.Count);

        Assert.Contains(fields, f => f.Key == "SignOff" && f.Label == "Sign-off" && f.DefaultValue == "Sincerely," && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "SignatoryName" && f.Label == "Signatory Name" && f.DefaultValue == "@ProjectManager.Name" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "SignatoryRole" && f.Label == "Signatory Role" && f.DefaultValue == "@ProjectManager.Position" && f.FieldType == "text");
        Assert.Contains(fields, f => f.Key == "SignatureImage" && f.Label == "Signature Image" && f.DefaultValue == "" && f.FieldType == "signature");
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_WithSignatureImage_RendersImgTag()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "LetterSignature",
            Properties = new Dictionary<string, string>
            {
                { "SignatureImage", "data:image/png;base64,SIGNATURE_PNG_DATA" }
            }
        };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.Contains("<img src=\"data:image/png;base64,SIGNATURE_PNG_DATA\" alt=\"Signature\"", html);
        Assert.DoesNotContain("border-bottom: 1px dashed #ccc", html);
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_WithoutSignatureImage_RendersDashedLineDiv()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "LetterSignature",
            Properties = new Dictionary<string, string>()
        };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.DoesNotContain("<img", html);
        Assert.Contains("<div style=\"height: 80px; width: 250px; border-bottom: 1px dashed #ccc; margin-bottom: 8px;\"></div>", html);
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_DefaultProperties_ResolvesContextTags()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance { Type = "LetterSignature" };
        var pm = CreateTestEmployee();

        string html = block.RenderHtml(instance, null, null, pm);

        Assert.Contains("Sincerely,", html);
        Assert.Contains("John Smith", html);
        Assert.Contains("Senior Robotics Engineer", html);
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_CustomOverriddenProperties_RendersCustomValues()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "LetterSignature",
            Properties = new Dictionary<string, string>
            {
                { "SignOff", "Kind regards," },
                { "SignatoryName", "Diana Prince" },
                { "SignatoryRole", "Director of Research" }
            }
        };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.Contains("Kind regards,", html);
        Assert.Contains("Diana Prince", html);
        Assert.Contains("Director of Research", html);
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_EmptySignOffAndRole_OmitsCorrespondingDivs()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance
        {
            Type = "LetterSignature",
            Properties = new Dictionary<string, string>
            {
                { "SignOff", "" },
                { "SignatoryName", "Arthur Curry" },
                { "SignatoryRole", "" },
                { "SignatureImage", "data:image/png;base64,ABC" }
            }
        };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.Contains("Arthur Curry", html);
        // Sign-off div should be omitted completely
        Assert.DoesNotContain("<div style=\"margin-bottom: 8px;\">", html);
        // Signatory role div should be omitted completely
        Assert.DoesNotContain("color: #555;", html);
    }

    [Fact]
    public void LetterSignatureBlock_RenderHtml_NullContextObjects_RendersGracefully()
    {
        var block = new LetterSignatureBlock();
        var instance = new DocumentBlockInstance { Type = "LetterSignature" };

        string html = block.RenderHtml(instance, null, null, null);

        Assert.NotNull(html);
        Assert.Contains("Sincerely,", html);
    }

    #endregion
}
