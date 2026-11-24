using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Hubs;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ChatSecurityServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly AlbumRepository _albums;
    private readonly AlbumService _albumService;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly Mock<IChatAuthorizationService> _mockChatAuth;
    private readonly ChatStateService _chatState;
    private readonly Mock<IFileService> _mockFileService;
    private readonly ServerEscrowService _escrowService;
    private readonly ChatSecurityService _service;

    public ChatSecurityServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_ChatSecurityTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _messages = new ChatMessageRepository(_persistence, _config, _companyProfiles);
        _albums = new AlbumRepository(_persistence, _config);

        _mockCrypto = new Mock<ICryptoService>();
        _mockCrypto.Setup(c => c.GenerateAesKeyBase64()).Returns("plain-aes-key-base64");
        _mockCrypto.Setup(c => c.EncryptRsa(It.IsAny<string>(), It.IsAny<string>()))
                   .Returns((string plain, string pubKey) => $"rsa_enc({plain})");
        _mockCrypto.Setup(c => c.DecryptRsa(It.IsAny<string>(), It.IsAny<string>()))
                   .Returns("plain-aes-key-base64");
        _mockCrypto.Setup(c => c.EncryptAes(It.IsAny<string>(), It.IsAny<string>()))
                   .Returns((string plain, string key) => $"aes_enc({plain})");
        _mockCrypto.Setup(c => c.DecryptAes(It.IsAny<string>(), It.IsAny<string>()))
                   .Returns((string cipher, string key) => cipher.Replace("aes_enc(", "").TrimEnd(')'));

        var serverConfigs = new ServerConfigRepository(_persistence, _config);
        _escrowService = new ServerEscrowService(_companyProfiles, serverConfigs, _mockCrypto.Object);

        _mockChatAuth = new Mock<IChatAuthorizationService>();
        _mockChatAuth.Setup(a => a.CanUserAccessChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>()))
                     .Returns(true);
        _mockChatAuth.Setup(a => a.CanUserManageChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>(), It.IsAny<bool>()))
                     .Returns(true);

        _albumService = new AlbumService(_albums, _channels, _mockCrypto.Object, _escrowService, _employees, _mockChatAuth.Object);

        _chatState = new ChatStateService();
        _mockFileService = new Mock<IFileService>();

        _service = new ChatSecurityService(
            _employees,
            _channels,
            _messages,
            _escrowService,
            _mockCrypto.Object,
            _albumService,
            _chatState,
            _mockChatAuth.Object,
            _mockFileService.Object,
            new Mock<ILogger<ChatSecurityService>>().Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenEnablingEncryption_GeneratesKeysAndEncryptsMessages()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", LastName = "Jones", IsActive = true, EncryptedPrivateKey = "vault-key-2", PublicKey = "pk-bob", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);
        _employees.Save(emp2);

        var channel = new ChatChannel
        {
            Id = "chan-1",
            ChannelType = ChatChannelType.General,
            IsEncrypted = false
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg-1",
            ChannelId = "chan-1",
            Content = "Hello World",
            IsEncrypted = false
        };
        _messages.Save(message);

        ChatChannel? notifiedChannel = null;
        _chatState.ChannelUpdated += ch => notifiedChannel = ch;

        // Act
        await _service.ToggleChannelEncryptionAsync("chan-1", true, "emp-1", privateKeyContext: "vault-key-1");

        // Assert
        var updatedChannel = _channels.GetById("chan-1");
        Assert.NotNull(updatedChannel);
        Assert.True(updatedChannel.IsEncrypted);
        Assert.True(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-1"));
        Assert.True(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-2"));

        var updatedMsg = _messages.GetById("msg-1");
        Assert.NotNull(updatedMsg);
        Assert.True(updatedMsg.IsEncrypted);
        Assert.Equal("aes_enc(Hello World)", updatedMsg.Content);

        Assert.NotNull(notifiedChannel);
        Assert.Equal("chan-1", notifiedChannel!.Id);
        Assert.True(notifiedChannel.IsEncrypted);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenEnablingEncryptionOnDMWithParticipantWithoutVaultPassword_ThrowsInvalidOperationException()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", Permissions = [AppPermissions.Chat.Use] };
        var emp2 = new Employee { Id = "emp-2", FirstName = "Bob", LastName = "Jones", IsActive = true, EncryptedPrivateKey = null };
        _employees.Save(emp1);
        _employees.Save(emp2);

        var channel = new ChatChannel
        {
            Id = "dm-1",
            ChannelType = ChatChannelType.Direct,
            CreatedById = "emp-1",
            ParticipantIds = new List<string> { "emp-1", "emp-2" },
            IsEncrypted = false
        };
        _channels.Save(channel);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.ToggleChannelEncryptionAsync("dm-1", true, "emp-1", privateKeyContext: "vault-key-1"));

        Assert.Equal("VAULT_PASSWORD_REQUIRED", ex.ErrorCode);
        Assert.Contains("Bob", ex.Message);
        Assert.Contains("Vault Password", ex.Message);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenDisablingEncryption_RequiresPrivateKeyAndDecryptsMessages()
    {
        // Arrange
        _employees.Save(new Employee { Id = "emp-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel
        {
            Id = "chan-2",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp-1"] = "rsa_enc(plain-aes-key-base64)" }
        };
        _channels.Save(channel);

        var message = new ChatMessage
        {
            Id = "msg-2",
            ChannelId = "chan-2",
            Content = "aes_enc(Secret Text)",
            IsEncrypted = true
        };
        _messages.Save(message);

        // Act
        await _service.ToggleChannelEncryptionAsync("chan-2", false, "emp-1", privateKeyContext: "my-priv-key");

        // Assert
        var updatedChannel = _channels.GetById("chan-2");
        Assert.NotNull(updatedChannel);
        Assert.False(updatedChannel.IsEncrypted);
        Assert.Empty(updatedChannel.EncryptedChannelKeys);

        var updatedMsg = _messages.GetById("msg-2");
        Assert.NotNull(updatedMsg);
        Assert.False(updatedMsg.IsEncrypted);
        Assert.Equal("Secret Text", updatedMsg.Content);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenDisablingWithoutPrivateKey_ThrowsInvalidOperationException()
    {
        // Arrange
        _employees.Save(new Employee { Id = "emp-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel
        {
            Id = "chan-3",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp-1"] = "rsa_enc(plain-aes-key-base64)" }
        };
        _channels.Save(channel);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.ToggleChannelEncryptionAsync("chan-3", false, "emp-1", privateKeyContext: null));
        Assert.Equal("PRIVATE_KEY_REQUIRED", ex.ErrorCode);
    }

    [Fact]
    public async Task ClaimEscrowedKeysAsync_WhenUserHasAccess_ClaimsKeySuccessfully()
    {
        // Arrange
        var emp = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp);

        var channel = new ChatChannel
        {
            Id = "chan-escrow-1",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["ServerMaster"] = "escrow-enc-key" }
        };
        _channels.Save(channel);

        _escrowService.EscrowPublicKey = "escrow-pub-key";
        _escrowService.DecryptedEscrowPrivateKey = "escrow-priv-key";

        _mockChatAuth.Setup(a => a.CanUserAccessChannel(channel, emp)).Returns(true);

        // Act
        await _service.ClaimEscrowedKeysAsync("emp-1");

        // Assert
        var updatedChannel = _channels.GetById("chan-escrow-1");
        Assert.NotNull(updatedChannel);
        Assert.True(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-1"));
    }

    [Fact]
    public async Task ClaimEscrowedKeysAsync_WhenUserLacksAccess_DoesNotClaimKey()
    {
        // Arrange
        var emp = new Employee { Id = "emp-unauth", FirstName = "Unauth", LastName = "User", IsActive = true, EncryptedPrivateKey = "vault-key-unauth", PublicKey = "pk-unauth", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp);

        var channel = new ChatChannel
        {
            Id = "chan-escrow-2",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["ServerMaster"] = "escrow-enc-key" }
        };
        _channels.Save(channel);

        _escrowService.EscrowPublicKey = "escrow-pub-key";
        _escrowService.DecryptedEscrowPrivateKey = "escrow-priv-key";

        _mockChatAuth.Setup(a => a.CanUserAccessChannel(channel, emp)).Returns(false);

        // Act
        await _service.ClaimEscrowedKeysAsync("emp-unauth");

        // Assert
        var updatedChannel = _channels.GetById("chan-escrow-2");
        Assert.NotNull(updatedChannel);
        Assert.False(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-unauth"));
    }

    [Fact]
    public async Task ShareKeyOpportunisticallyAsync_WhenUserHasAccess_SharesKey()
    {
        // Arrange
        var emp = new Employee { Id = "emp-share", FirstName = "Share", LastName = "User", IsActive = true, EncryptedPrivateKey = "vault-key-share", PublicKey = "pk-share", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp);

        var channel = new ChatChannel
        {
            Id = "chan-share-1",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>()
        };
        _channels.Save(channel);

        _mockChatAuth.Setup(a => a.CanUserAccessChannel(It.IsAny<ChatChannel>(), emp)).Returns(true);

        // Act
        await _service.ShareKeyOpportunisticallyAsync("chan-share-1", "plain-key");

        // Assert
        var updatedChannel = _channels.GetById("chan-share-1");
        Assert.NotNull(updatedChannel);
        Assert.True(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-share"));
    }

    [Fact]
    public async Task ShareKeyOpportunisticallyAsync_WhenUserLacksAccess_DoesNotShareKey()
    {
        // Arrange
        var emp = new Employee { Id = "emp-no-share", FirstName = "NoShare", LastName = "User", IsActive = true, EncryptedPrivateKey = "vault-key-noshare", PublicKey = "pk-no-share", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp);

        var channel = new ChatChannel
        {
            Id = "chan-share-2",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>()
        };
        _channels.Save(channel);

        _mockChatAuth.Setup(a => a.CanUserAccessChannel(It.IsAny<ChatChannel>(), emp)).Returns(false);

        // Act
        await _service.ShareKeyOpportunisticallyAsync("chan-share-2", "plain-key");

        // Assert
        var updatedChannel = _channels.GetById("chan-share-2");
        Assert.NotNull(updatedChannel);
        Assert.False(updatedChannel.EncryptedChannelKeys.ContainsKey("emp-no-share"));
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_UnencryptedChannel_ReturnsMatchingMessages()
    {
        // Arrange
        _employees.Save(new Employee { Id = "user-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel { Id = "chan-search-1", Name = "General", IsEncrypted = false };
        _channels.Save(channel);

        var msg1 = new ChatMessage { Id = "msg-1", ChannelId = "chan-search-1", Content = "The quick brown fox", SentAt = DateTime.UtcNow };
        var msg2 = new ChatMessage { Id = "msg-2", ChannelId = "chan-search-1", Content = "Lazy dog jumps", SentAt = DateTime.UtcNow };
        _messages.Save(msg1);
        _messages.Save(msg2);

        // Act
        var results = await _service.SecureSearchMessagesAsync("chan-search-1", "brown", "user-1");

        // Assert
        Assert.Single(results);
        Assert.Equal("msg-1", results[0].Id);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_EncryptedChannel_WithUserPrivateKey_DecryptsAndFindsMatches()
    {
        // Arrange
        _employees.Save(new Employee { Id = "user-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel
        {
            Id = "chan-search-enc",
            Name = "Encrypted Channel",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["user-1"] = "rsa_enc(plain-aes-key-base64)" }
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "msg-enc-1",
            ChannelId = "chan-search-enc",
            Content = "aes_enc(confidential quarterly results)",
            IsEncrypted = true,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(msg);

        // Act
        var results = await _service.SecureSearchMessagesAsync("chan-search-enc", "quarterly", "user-1", "user-1-priv-key");

        // Assert
        Assert.Single(results);
        Assert.Equal("msg-enc-1", results[0].Id);
        Assert.Equal("confidential quarterly results", results[0].Content);
        Assert.False(results[0].IsEncrypted);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_EncryptedChannel_WithEscrowFallback_DecryptsAndFindsMatches()
    {
        // Arrange
        _employees.Save(new Employee { Id = "user-auditor", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel
        {
            Id = "chan-search-escrow",
            Name = "Escrow Channel",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["ServerMaster"] = "escrow-enc-key" }
        };
        _channels.Save(channel);

        _escrowService.EscrowPublicKey = "escrow-pub-key";
        _escrowService.DecryptedEscrowPrivateKey = "escrow-priv-key";

        var msg = new ChatMessage
        {
            Id = "msg-escrow-1",
            ChannelId = "chan-search-escrow",
            Content = "aes_enc(critical escrow report)",
            IsEncrypted = true,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(msg);

        // Act - user has unlocked vault but no personal key, falls back to escrow
        var results = await _service.SecureSearchMessagesAsync("chan-search-escrow", "escrow", "user-auditor", "auditor-unlocked-priv-key");

        // Assert
        Assert.Single(results);
        Assert.Equal("msg-escrow-1", results[0].Id);
        Assert.Equal("critical escrow report", results[0].Content);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_EncryptedChannel_WithoutKeyOrEscrow_ReturnsEmpty()
    {
        // Arrange
        var channel = new ChatChannel
        {
            Id = "chan-search-nokey",
            Name = "No Key Channel",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>()
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "msg-nokey-1",
            ChannelId = "chan-search-nokey",
            Content = "aes_enc(hidden secret)",
            IsEncrypted = true,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(msg);

        // Act
        var results = await _service.SecureSearchMessagesAsync("chan-search-nokey", "hidden", "user-1");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_DirectChannel_WhenRequestingUserNotInParticipants_ReturnsEmpty()
    {
        // Arrange
        var channel = new ChatChannel
        {
            Id = "dm-restricted",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user-a", "user-b" },
            IsEncrypted = false
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "msg-dm-1",
            ChannelId = "dm-restricted",
            Content = "private conversation",
            SentAt = DateTime.UtcNow
        };
        _messages.Save(msg);

        // Act - user-c is not a participant
        var results = await _service.SecureSearchMessagesAsync("dm-restricted", "private", "user-c");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_WithIsPictureFilter_ReturnsOnlyMessagesWithPictures()
    {
        // Arrange
        _employees.Save(new Employee { Id = "user-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
        var channel = new ChatChannel { Id = "chan-filter-pic", Name = "General", IsEncrypted = false };
        _channels.Save(channel);

        var msgText = new ChatMessage { Id = "msg-text", ChannelId = "chan-filter-pic", Content = "plain text", SentAt = DateTime.UtcNow };
        var msgPic = new ChatMessage
        {
            Id = "msg-pic",
            ChannelId = "chan-filter-pic",
            Content = "look at this",
            Attachments = [new ChatAttachment { Id = "att-1", FileName = "screenshot.png", ContentType = "image/png" }],
            SentAt = DateTime.UtcNow
        };
        _messages.Save(msgText);
        _messages.Save(msgPic);

        // Act
        var results = await _service.SecureSearchMessagesAsync("chan-filter-pic", "isPicture:", "user-1");

        // Assert
        Assert.Single(results);
        Assert.Equal("msg-pic", results[0].Id);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_WithFromFilter_ReturnsOnlyMessagesFromSender()
    {
        // Arrange
        var empAlice = new Employee { Id = "emp-alice", FirstName = "Alice", LastName = "Smith", IsActive = true };
        var empBob = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Jones", IsActive = true };
        _employees.Save(empAlice);
        _employees.Save(empBob);

        var channel = new ChatChannel { Id = "chan-filter-from", Name = "General", IsEncrypted = false };
        _channels.Save(channel);

        var msgAlice = new ChatMessage { Id = "msg-a", ChannelId = "chan-filter-from", SenderId = "emp-alice", Content = "roadmap discussion", SentAt = DateTime.UtcNow };
        var msgBob = new ChatMessage { Id = "msg-b", ChannelId = "chan-filter-from", SenderId = "emp-bob", Content = "roadmap budget", SentAt = DateTime.UtcNow };
        _messages.Save(msgAlice);
        _messages.Save(msgBob);

        // Act - search with from:Alice
        var results = await _service.SecureSearchMessagesAsync("chan-filter-from", "from:Alice roadmap", "emp-alice");

        // Assert
        Assert.Single(results);
        Assert.Equal("msg-a", results[0].Id);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_WithFromAdmin_ReturnsAdminMessages()
    {
        // Arrange
        var adminEmp = new Employee { Id = "emp-admin", FirstName = "Alex", LastName = "Tremblay", IsAdmin = true, IsActive = true };
        var memberEmp = new Employee { Id = "emp-member", FirstName = "Alice", LastName = "Smith", IsAdmin = false, IsActive = true };
        _employees.Save(adminEmp);
        _employees.Save(memberEmp);

        var channel = new ChatChannel { Id = "chan-filter-admin", Name = "General", IsEncrypted = false };
        _channels.Save(channel);

        var msgAdmin = new ChatMessage { Id = "msg-adm-1", ChannelId = "chan-filter-admin", SenderId = "emp-admin", Content = "Server maintenance tonight", SentAt = DateTime.UtcNow };
        var msgMember = new ChatMessage { Id = "msg-mem-1", ChannelId = "chan-filter-admin", SenderId = "emp-member", Content = "Server status ok", SentAt = DateTime.UtcNow };
        _messages.Save(msgAdmin);
        _messages.Save(msgMember);

        // Act - search with from:admin (no space)
        var resultsNoSpace = await _service.SecureSearchMessagesAsync("chan-filter-admin", "from:admin server", "emp-member");

        // Act - search with from: admin (with space)
        var resultsWithSpace = await _service.SecureSearchMessagesAsync("chan-filter-admin", "from: admin server", "emp-member");

        // Assert
        Assert.Single(resultsNoSpace);
        Assert.Equal("msg-adm-1", resultsNoSpace[0].Id);

        Assert.Single(resultsWithSpace);
        Assert.Equal("msg-adm-1", resultsWithSpace[0].Id);
    }

    [Fact]
    public async Task SecureSearchMessagesAsync_WithNullChannelId_SearchesAcrossAccessibleChannels()
    {
        // Arrange
        var chan1 = new ChatChannel { Id = "chan-all-1", Name = "General", IsEncrypted = false };
        var chan2 = new ChatChannel { Id = "chan-all-2", Name = "Random", IsEncrypted = false };
        _channels.Save(chan1);
        _channels.Save(chan2);

        _mockChatAuth.Setup(a => a.GetChannelsForUser("emp-user")).Returns([chan1, chan2]);

        var msg1 = new ChatMessage { Id = "msg-all-1", ChannelId = "chan-all-1", Content = "cross-channel search term", SentAt = DateTime.UtcNow.AddMinutes(-5) };
        var msg2 = new ChatMessage { Id = "msg-all-2", ChannelId = "chan-all-2", Content = "another search term here", SentAt = DateTime.UtcNow };
        _messages.Save(msg1);
        _messages.Save(msg2);

        // Act - null channelId defaults to all accessible channels
        var results = await _service.SecureSearchMessagesAsync(null, "search", "emp-user");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "msg-all-1");
        Assert.Contains(results, r => r.Id == "msg-all-2");
    }
    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenChannelAlreadyMarkedEncryptedWithZeroKeys_HealsChannelAndDistributesKeys()
    {
        // Arrange: Corrupted channel state created when channel was saved with IsEncrypted = true before toggle
        var emp1 = new Employee { Id = "emp-heal-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        var emp2 = new Employee { Id = "emp-heal-2", FirstName = "Bob", LastName = "Jones", IsActive = true, EncryptedPrivateKey = "vault-key-2", PublicKey = "pk-bob", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);
        _employees.Save(emp2);

        var channel = new ChatChannel
        {
            Id = "chan-heal-1",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true, // Prematurely marked encrypted!
            EncryptedChannelKeys = new Dictionary<string, string>() // But 0 keys!
        };
        _channels.Save(channel);

        // Act
        await _service.ToggleChannelEncryptionAsync("chan-heal-1", true, "emp-heal-1", privateKeyContext: "vault-key-1");

        // Assert
        var healedChannel = _channels.GetById("chan-heal-1");
        Assert.NotNull(healedChannel);
        Assert.True(healedChannel.IsEncrypted);
        Assert.True(healedChannel.EncryptedChannelKeys.ContainsKey("emp-heal-1"));
        Assert.True(healedChannel.EncryptedChannelKeys.ContainsKey("emp-heal-2"));
        if (_escrowService.IsEscrowAvailable)
        {
            Assert.True(healedChannel.EncryptedChannelKeys.ContainsKey("ServerMaster"));
        }
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenEnablingWithoutPrivateKey_ThrowsDomainException()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);

        var channel = new ChatChannel
        {
            Id = "chan-no-privkey",
            ChannelType = ChatChannelType.General,
            IsEncrypted = false
        };
        _channels.Save(channel);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.ToggleChannelEncryptionAsync("chan-no-privkey", true, "emp-1", privateKeyContext: null));

        Assert.Equal("VAULT_LOCKED", ex.ErrorCode);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenEnabling_EncryptsAllAttachmentsAndThumbnailsOnDisk()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-att-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);

        var channel = new ChatChannel
        {
            Id = "chan-att-1",
            ChannelType = ChatChannelType.General,
            IsEncrypted = false
        };
        _channels.Save(channel);

        var attachmentFilePath = Path.Combine(_testDataDir, "test-attachment.png");
        var thumbFilePath = Path.Combine(_testDataDir, "test-attachment-thumb.png");
        File.WriteAllText(attachmentFilePath, "fake image bytes");
        File.WriteAllText(thumbFilePath, "fake thumb bytes");

        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-att-1", "test-attachment.png", false))
                        .Returns(attachmentFilePath);
        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-att-1", "test-attachment.png", true))
                        .Returns(thumbFilePath);
        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "chan-att-1", ""))
                        .Returns(Path.Combine(_testDataDir, "empty-chat-folder"));

        var message = new ChatMessage
        {
            Id = "msg-att-1",
            ChannelId = "chan-att-1",
            Content = "Look at this image",
            IsEncrypted = false,
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment
                {
                    FileName = "test-attachment.png",
                    FilePath = "/spokesapi/files/chat/chan-att-1/test-attachment.png",
                    HasServerThumbnail = true,
                    ContentType = "image/png"
                }
            }
        };
        _messages.Save(message);

        // Act
        await _service.ToggleChannelEncryptionAsync("chan-att-1", true, "emp-att-1", privateKeyContext: "vault-key-1");

        // Assert
        _mockFileService.Verify(f => f.EncryptFileAsync(attachmentFilePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockFileService.Verify(f => f.EncryptFileAsync(thumbFilePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenCalledOnAlreadyEncryptedChannel_PerformsIdempotentAttachmentSweep()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-sync-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);

        var channel = new ChatChannel
        {
            Id = "chan-sync-1",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp-sync-1"] = "rsa_enc(plain-aes-key-base64)" }
        };
        _channels.Save(channel);

        var attachmentFilePath = Path.Combine(_testDataDir, "sync-attachment.png");
        File.WriteAllText(attachmentFilePath, "unencrypted legacy photo");

        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-sync-1", "sync-attachment.png", false))
                        .Returns(attachmentFilePath);
        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-sync-1", "sync-attachment.png", true))
                        .Returns(attachmentFilePath);
        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "chan-sync-1", ""))
                        .Returns(Path.Combine(_testDataDir, "empty-chat-folder"));

        var message = new ChatMessage
        {
            Id = "msg-sync-1",
            ChannelId = "chan-sync-1",
            Content = "aes_enc(Existing encrypted message)",
            IsEncrypted = true,
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment
                {
                    FileName = "sync-attachment.png",
                    FilePath = "/spokesapi/files/chat/chan-sync-1/sync-attachment.png",
                    ContentType = "image/png"
                }
            }
        };
        _messages.Save(message);

        // Act - Call toggle with enableEncryption: true on already encrypted channel (Resync)
        await _service.ToggleChannelEncryptionAsync("chan-sync-1", true, "emp-sync-1", privateKeyContext: "vault-key-1");

        // Assert: Attachment was processed for encryption
        _mockFileService.Verify(f => f.EncryptFileAsync(attachmentFilePath, "plain-aes-key-base64", It.IsAny<CancellationToken>()), Times.Once);
        // And channel remains encrypted
        var refreshed = _channels.GetById("chan-sync-1");
        Assert.NotNull(refreshed);
        Assert.True(refreshed.IsEncrypted);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenDisabling_DecryptsAllAttachmentsAndThumbnailsOnDisk()
    {
        // Arrange
        var emp1 = new Employee { Id = "emp-dec-1", FirstName = "Alice", LastName = "Smith", IsActive = true, EncryptedPrivateKey = "vault-key-1", PublicKey = "pk-alice", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(emp1);

        var channel = new ChatChannel
        {
            Id = "chan-dec-1",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp-dec-1"] = "rsa_enc(plain-aes-key-base64)" }
        };
        _channels.Save(channel);

        var attachmentFilePath = Path.Combine(_testDataDir, "dec-attachment.png");
        File.WriteAllText(attachmentFilePath, "encrypted image bytes");

        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-dec-1", "dec-attachment.png", false))
                        .Returns(attachmentFilePath);
        _mockFileService.Setup(f => f.GetAttachmentPhysicalPath("chan-dec-1", "dec-attachment.png", true))
                        .Returns(attachmentFilePath);
        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "chan-dec-1", ""))
                        .Returns(Path.Combine(_testDataDir, "empty-chat-folder"));

        var message = new ChatMessage
        {
            Id = "msg-dec-1",
            ChannelId = "chan-dec-1",
            Content = "aes_enc(Encrypted text)",
            IsEncrypted = true,
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment
                {
                    FileName = "dec-attachment.png",
                    FilePath = "/spokesapi/files/chat/chan-dec-1/dec-attachment.png",
                    ContentType = "image/png"
                }
            }
        };
        _messages.Save(message);

        // Act
        await _service.ToggleChannelEncryptionAsync("chan-dec-1", false, "emp-dec-1", privateKeyContext: "vault-key-1");

        // Assert
        _mockFileService.Verify(f => f.DecryptFileAsync(attachmentFilePath, "plain-aes-key-base64", It.IsAny<CancellationToken>()), Times.Once);
        var refreshed = _channels.GetById("chan-dec-1");
        Assert.NotNull(refreshed);
        Assert.False(refreshed.IsEncrypted);
    }

    [Fact]
    public void IsPotentialBase64Cipher_DifferentiatesRealCipherFromPlaintext()
    {
        var crypto = new CryptoService();
        var key = crypto.GenerateAesKeyBase64();
        var realCipher = crypto.EncryptAes("Hello secure world", key);

        // Real AES cipher is valid base64 >= 32 bytes aligned to 16 bytes
        Assert.True(ChatSecurityService.IsPotentialBase64Cipher(realCipher));

        // Plaintext messages must not be classified as ciphers
        Assert.False(ChatSecurityService.IsPotentialBase64Cipher("Hello Alice"));
        Assert.False(ChatSecurityService.IsPotentialBase64Cipher("This channel is encrypted and you are not on the access list to view its contents."));
        Assert.False(ChatSecurityService.IsPotentialBase64Cipher("Encrypted message"));
        Assert.False(ChatSecurityService.IsPotentialBase64Cipher(""));
        Assert.False(ChatSecurityService.IsPotentialBase64Cipher(null));
    }

    [Fact]
    public async Task RotateAndSearch_MultiEpochEncryptedChannel_PreservesHistoricalSearchAndExcludesEvictedUsers()
    {
        var crypto = new CryptoService();
        var (pubAlice, privAlice) = crypto.GenerateRsaKeyPair();
        var (pubEve, privEve) = crypto.GenerateRsaKeyPair();

        var alice = new Employee { Id = "emp-epoch-alice", IsActive = true, PublicKey = pubAlice, EncryptedPrivateKey = "enc-alice", Permissions = [AppPermissions.Chat.Use] };
        var eve = new Employee { Id = "emp-epoch-eve", IsActive = true, PublicKey = pubEve, EncryptedPrivateKey = "enc-eve", Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(alice);
        _employees.Save(eve);

        var teams = new TeamRepository(_persistence, _config);
        var chatAuth = new ChatAuthorizationService(_channels, new ProjectRepository(_persistence, _config), teams, _employees);
        var escrowService = new ServerEscrowService(_companyProfiles, new ServerConfigRepository(_persistence, _config), crypto);
        var realAlbumService = new AlbumService(_albums, _channels, crypto, escrowService, _employees, chatAuth);
        var service = new ChatSecurityService(
            _employees,
            _channels,
            _messages,
            escrowService,
            crypto,
            realAlbumService,
            _chatState,
            chatAuth,
            _mockFileService.Object,
            new Mock<ILogger<ChatSecurityService>>().Object);

        var key0 = crypto.GenerateAesKeyBase64();
        var channel = new ChatChannel
        {
            Id = "chan-multi-epoch",
            Name = "Epoch Search Room",
            ChannelType = ChatChannelType.Group,
            CreatedById = alice.Id,
            IsEncrypted = true,
            CurrentKeyEpoch = 0,
            ParticipantIds = [alice.Id, eve.Id],
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                [alice.Id] = crypto.EncryptRsa(key0, pubAlice),
                [eve.Id] = crypto.EncryptRsa(key0, pubEve)
            }
        };
        _channels.Save(channel);

        _messages.Save(new ChatMessage
        {
            Id = "m-epoch-0",
            ChannelId = channel.Id,
            SenderId = alice.Id,
            Content = crypto.EncryptAes("Alpha epochzero keyword", key0),
            IsEncrypted = true,
            KeyEpoch = 0,
            SentAt = DateTime.UtcNow.AddMinutes(-2)
        });

        // Evict Eve and rotate to Epoch 1
        channel.ParticipantIds.Remove(eve.Id);
        Assert.True(service.RotateChannelKeyOnEviction(channel, new[] { eve.Id }, new[] { alice }));
        _channels.Save(channel);

        Assert.True(channel.TryGetEncryptedKeyForEpoch(1, alice.Id, out var encKey1));
        var key1 = crypto.DecryptRsa(encKey1!, privAlice);

        _messages.Save(new ChatMessage
        {
            Id = "m-epoch-1",
            ChannelId = channel.Id,
            SenderId = alice.Id,
            Content = crypto.EncryptAes("Omega epochone keyword", key1),
            IsEncrypted = true,
            KeyEpoch = 1,
            SentAt = DateTime.UtcNow
        });

        // Alice can search and find messages from BOTH Epoch 0 and Epoch 1
        var aliceMatches = await service.SecureSearchMessagesAsync(channel.Id, "keyword", alice.Id, privAlice);
        Assert.Equal(2, aliceMatches.Count);
        Assert.Contains(aliceMatches, m => m.Id == "m-epoch-0" && m.Content == "Alpha epochzero keyword");
        Assert.Contains(aliceMatches, m => m.Id == "m-epoch-1" && m.Content == "Omega epochone keyword");

        // Evicted Eve finds nothing even with her private key
        var eveMatches = await service.SecureSearchMessagesAsync(channel.Id, "keyword", eve.Id, privEve);
        Assert.Empty(eveMatches);
        Assert.False(channel.HasAnyEpochKey(eve.Id));
    }

    [Fact]
    public async Task ClaimEscrowedKeysAsync_NotifiesChatStateChannelUpdated()
    {
        var realCrypto = new CryptoService();
        var (pubUser, _) = realCrypto.GenerateRsaKeyPair();

        var profile = _companyProfiles.Get();
        profile.PublicChannelMasterPassword = "escrow-test-password";
        _companyProfiles.Save(profile);

        var escrowService = new ServerEscrowService(_companyProfiles, new ServerConfigRepository(_persistence, _config), realCrypto, configuration: _config);
        escrowService.Initialize();

        var user = new Employee
        {
            Id = "emp-claim",
            FirstName = "Claire",
            LastName = "Claim",
            IsActive = true,
            EncryptedPrivateKey = "vault-key-claim",
            PublicKey = pubUser,
            Permissions = [AppPermissions.Chat.Use]
        };
        _employees.Save(user);

        var plainChannelKey = realCrypto.GenerateAesKeyBase64();
        var wrappedEscrow = realCrypto.EncryptRsa(plainChannelKey, escrowService.EscrowPublicKey!);
        var channel = new ChatChannel
        {
            Id = "chan-escrow-notify",
            Name = "Escrow Notify",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            CurrentKeyEpoch = 0,
            EncryptedChannelKeys = new Dictionary<string, string> { ["ServerMaster"] = wrappedEscrow },
            EncryptedEpochKeys = new Dictionary<int, Dictionary<string, string>>
            {
                [0] = new Dictionary<string, string> { ["ServerMaster"] = wrappedEscrow }
            }
        };
        _channels.Save(channel);

        var chatState = new ChatStateService();
        ChatChannel? notified = null;
        chatState.ChannelUpdated += ch => notified = ch;

        var albumService = new AlbumService(_albums, _channels, realCrypto, escrowService, _employees, _mockChatAuth.Object);
        var service = new ChatSecurityService(
            _employees,
            _channels,
            _messages,
            escrowService,
            realCrypto,
            albumService,
            chatState,
            _mockChatAuth.Object,
            _mockFileService.Object,
            new Mock<ILogger<ChatSecurityService>>().Object);

        await service.ClaimEscrowedKeysAsync(user.Id);

        Assert.NotNull(notified);
        Assert.Equal("chan-escrow-notify", notified!.Id);
        Assert.True(notified.EncryptedChannelKeys.ContainsKey(user.Id));
    }

    [Fact]
    public async Task ShareKeyOpportunisticallyAsync_NotifiesChatStateChannelUpdated()
    {
        var waitingUser = new Employee
        {
            Id = "emp-wait",
            FirstName = "Wade",
            LastName = "Waiting",
            IsActive = true,
            EncryptedPrivateKey = "vault-key-wait",
            PublicKey = "pub-wait",
            Permissions = [AppPermissions.Chat.Use]
        };
        _employees.Save(waitingUser);

        var channel = new ChatChannel
        {
            Id = "chan-opp-notify",
            Name = "DM",
            ChannelType = ChatChannelType.Direct,
            IsEncrypted = true,
            CurrentKeyEpoch = 0,
            ParticipantIds = ["emp-sender", waitingUser.Id],
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp-sender"] = "rsa_enc(key)" }
        };
        _channels.Save(channel);

        ChatChannel? notified = null;
        _chatState.ChannelUpdated += ch => notified = ch;

        await _service.ShareKeyOpportunisticallyAsync(channel.Id, "plain-aes-key", expectedEpoch: 0);

        Assert.NotNull(notified);
        Assert.Equal("chan-opp-notify", notified!.Id);
        Assert.True(notified.EncryptedChannelKeys.ContainsKey(waitingUser.Id));
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_WhenAlreadyEncrypted_MediaSyncNotifiesChatStateChannelUpdated()
    {
        var admin = new Employee
        {
            Id = "emp-media-sync",
            FirstName = "Maya",
            LastName = "Sync",
            IsActive = true,
            IsAdmin = true,
            EncryptedPrivateKey = "vault-key-maya",
            PublicKey = "pub-maya",
            Permissions = [AppPermissions.Chat.Use]
        };
        _employees.Save(admin);

        var channel = new ChatChannel
        {
            Id = "chan-media-notify",
            Name = "Media",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            CurrentKeyEpoch = 0,
            EncryptedChannelKeys = new Dictionary<string, string> { [admin.Id] = "rsa_enc(plain-aes-key)" }
        };
        _channels.Save(channel);

        ChatChannel? notified = null;
        _chatState.ChannelUpdated += ch => notified = ch;

        await _service.ToggleChannelEncryptionAsync(channel.Id, enableEncryption: true, requestingUserId: admin.Id, privateKeyContext: "priv-maya");

        Assert.NotNull(notified);
        Assert.Equal("chan-media-notify", notified!.Id);
    }
}
