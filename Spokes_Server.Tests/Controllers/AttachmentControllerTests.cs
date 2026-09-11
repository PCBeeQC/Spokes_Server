using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;

namespace Spokes_Server.Tests.Controllers
{
    public class AttachmentControllerTests : TestDataTestBase
    {
        private readonly AttachmentController _controller;
        private readonly ChatChannelRepository _channelsRepo;

        public AttachmentControllerTests()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            _channelsRepo = new ChatChannelRepository(writer, mockConfig.Object);

            var employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
            var openIdAccountRepo = new OpenIdAccountRepository(writer, mockConfig.Object);
            var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();

            var userPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
                new Claim(ClaimTypes.NameIdentifier, "user1"),
                new Claim("sub", "user1")
            }, "mock"));

            mockAuthStateProvider
                .Setup(a => a.GetAuthenticationStateAsync())
                .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(userPrincipal));

            var userService = new UserService(mockAuthStateProvider.Object, employeeRepo, openIdAccountRepo);

            // Seed user employee and OIDC linkage
            var emp = new Employee { Id = "user1", FirstName = "Test", LastName = "User" };
            employeeRepo.Save(emp);
            var linkage = new OpenIdAccount { Id = "link-1", Sub = "user1", LinkedEmployeeId = "user1" };
            openIdAccountRepo.Save(linkage);

            var chatServiceMock = new Mock<IChatChannelAccessService>();
            
            chatServiceMock.Setup(s => s.GetChannelsForUser(It.IsAny<string>()))
                .Returns((string uid) => _channelsRepo.GetDirectChannelsForUser(uid).Concat(_channelsRepo.GetChannelsForUser(uid, new List<string>(), new List<string>())).ToList());

            var chatProvider = new ChatFileAccessProvider(chatServiceMock.Object);
            var providers = new List<IFileAccessProvider> { chatProvider };

            _controller = new AttachmentController(mockConfig.Object, _channelsRepo, providers, userService);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = userPrincipal }
            };
        }

        [Fact]
        public async Task GetAttachment_ReturnsNotFound_WhenChannelDoesNotExist()
        {
            var result = await _controller.GetAttachment("invalid-channel", "file.png");
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task GetAttachment_ReturnsForbid_WhenUserNotParticipant()
        {
            var channel = new ChatChannel { Id = "chan1", ChannelType = ChatChannelType.Direct, ParticipantIds = new List<string> { "user2", "user3" } };
            _channelsRepo.Save(channel);

            var result = await _controller.GetAttachment("chan1", "file.png");
            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetAttachment_ReturnsNotFound_WhenFileDoesNotExist()
        {
            var channel = new ChatChannel { Id = "chan1", ChannelType = ChatChannelType.General, ParticipantIds = new List<string> { "user1", "user2" } };
            _channelsRepo.Save(channel);

            var result = await _controller.GetAttachment("chan1", "file.png");
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task GetAttachment_ReturnsPhysicalFile_WhenAuthorizedAndFileExists()
        {
            var channel = new ChatChannel { Id = "chan1", ChannelType = ChatChannelType.General };
            _channelsRepo.Save(channel);

            var attachmentsDir = Path.Combine(_testDataPath, "Attachments", "chan1");
            Directory.CreateDirectory(attachmentsDir);
            var filePath = Path.Combine(attachmentsDir, "test.txt");
            File.WriteAllText(filePath, "test content");

            var result = await _controller.GetAttachment("chan1", "test.txt");
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("text/plain", physicalFileResult.ContentType);
        }
    }
}

