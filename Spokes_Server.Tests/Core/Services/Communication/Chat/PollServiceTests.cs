using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class PollServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ChatPollRepository _polls;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly Mock<IChatRealtimeGateway> _mockRealtimeGateway;
    private readonly ChatStateService _chatState;
    private readonly CryptoService _crypto;
    private readonly PollService _service;

    public PollServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Polls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _polls = new ChatPollRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _projects = new ProjectRepository(_persistence, _config);

        _mockRealtimeGateway = new Mock<IChatRealtimeGateway>();
        _chatState = new ChatStateService();
        _crypto = new CryptoService();

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ChatPolls).Returns(_polls);
        commContext.Setup(c => c.ChatChannels).Returns(_channels);

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);
        hrContext.Setup(h => h.Teams).Returns(_teams);

        var projectsContext = new Mock<ProjectsContext>();
        projectsContext.Setup(p => p.Projects).Returns(_projects);

        _service = new PollService(
            commContext.Object,
            hrContext.Object,
            projectsContext.Object,
            realtimeGateway: _mockRealtimeGateway.Object,
            chatState: _chatState,
            crypto: _crypto);
    }

    private (ChatChannel Channel, Employee Creator, Employee Voter1, Employee Voter2) SeedBasicChannelAndUsers(bool isEncrypted = false)
    {
        var creator = new Employee { Id = "emp-creator", FirstName = "Alice", LastName = "Smith", IsAdmin = false };
        var voter1 = new Employee { Id = "emp-voter1", FirstName = "Bob", LastName = "Jones", IsAdmin = false };
        var voter2 = new Employee { Id = "emp-voter2", FirstName = "Charlie", LastName = "Brown", IsAdmin = false };
        _employees.Save(creator);
        _employees.Save(voter1);
        _employees.Save(voter2);

        var channel = new ChatChannel
        {
            Id = "chan-1",
            Name = "general",
            ChannelType = ChatChannelType.General,
            IsEncrypted = isEncrypted,
            EncryptedChannelKeys = isEncrypted
                ? new Dictionary<string, string>
                {
                    [creator.Id] = "wrapped-key-creator",
                    [voter1.Id] = "wrapped-key-voter1",
                    [voter2.Id] = "wrapped-key-voter2"
                }
                : new Dictionary<string, string>()
        };
        _channels.Save(channel);

        return (channel, creator, voter1, voter2);
    }

    [Fact]
    public void CreatePoll_WithValidInput_PersistsPollAndNotifiesChannel()
    {
        var (channel, creator, _, _) = SeedBasicChannelAndUsers();

        var result = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Where should we go for dinner?",
                Options =
                [
                    new() { Text = "Sushi", Emoji = "🍣" },
                    new() { Text = "Pizza", Emoji = "🍕" }
                ],
                AllowMultipleAnswers = false,
                AllowAddOptions = true,
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            },
            creator.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Where should we go for dinner?", result.Value!.Question);
        Assert.Equal(2, result.Value.Options.Count);
        Assert.True(result.Value.AllowAddOptions);
        Assert.False(result.Value.AllowMultipleAnswers);
        Assert.NotNull(result.Value.ExpiresAt);

        var persisted = _polls.GetById(result.Value.Id);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.Options.Count);
        Assert.Equal("Sushi", persisted.Options[0].Text);
        Assert.Equal("🍣", persisted.Options[0].Emoji);
    }

    [Fact]
    public void CreatePoll_WithFewerThanTwoOptions_ReturnsFailure()
    {
        var (channel, creator, _, _) = SeedBasicChannelAndUsers();

        var result = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Invalid poll?",
                Options = [new() { Text = "Only One" }]
            },
            creator.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_ARGUMENT", result.Code);
    }

    [Fact]
    public void CreatePoll_InEncryptedChannel_EncryptsOnDiskAndDecryptsForDisplay()
    {
        var (channel, creator, _, _) = SeedBasicChannelAndUsers(isEncrypted: true);
        var plainKey = _crypto.GenerateAesKeyBase64();

        var result = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Secret Question",
                Options =
                [
                    new() { Text = "Secret A" },
                    new() { Text = "Secret B" }
                ]
            },
            creator.Id,
            plainChannelKey: plainKey);

        Assert.True(result.IsSuccess);
        var onDisk = _polls.GetById(result.Value!.Id)!;
        Assert.True(onDisk.IsEncrypted);
        Assert.NotEqual("Secret Question", onDisk.Question);
        Assert.NotEqual("Secret A", onDisk.Options[0].Text);

        var decrypted = _service.DecryptPollForDisplay(onDisk, plainKey);
        Assert.Equal("Secret Question", decrypted.Question);
        Assert.Equal("Secret A", decrypted.Options[0].Text);
        Assert.Equal("Secret B", decrypted.Options[1].Text);
    }

    [Fact]
    public void ToggleVote_SingleChoice_MovesVoteBetweenOptionsAndUntoggles()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Best language?",
                Options =
                [
                    new() { Id = "opt-1", Text = "C#" },
                    new() { Id = "opt-2", Text = "Rust" }
                ],
                AllowMultipleAnswers = false
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        // Vote for Option 1
        var vote1 = _service.ToggleVote(pollId, "opt-1", voter1.Id);
        Assert.True(vote1.IsSuccess);
        Assert.Contains(voter1.Id, _polls.GetById(pollId)!.Options[0].VoterIds);

        // Switch vote to Option 2 (should remove from Option 1)
        var vote2 = _service.ToggleVote(pollId, "opt-2", voter1.Id);
        Assert.True(vote2.IsSuccess);
        var updated = _polls.GetById(pollId)!;
        Assert.DoesNotContain(voter1.Id, updated.Options[0].VoterIds);
        Assert.Contains(voter1.Id, updated.Options[1].VoterIds);

        // Click Option 2 again to remove vote
        var unvote = _service.ToggleVote(pollId, "opt-2", voter1.Id);
        Assert.True(unvote.IsSuccess);
        updated = _polls.GetById(pollId)!;
        Assert.Empty(updated.Options[0].VoterIds);
        Assert.Empty(updated.Options[1].VoterIds);
    }

    [Fact]
    public void ToggleVote_MultipleChoice_AllowsSelectingMultipleOptions()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Available days?",
                Options =
                [
                    new() { Id = "opt-mon", Text = "Monday" },
                    new() { Id = "opt-tue", Text = "Tuesday" }
                ],
                AllowMultipleAnswers = true
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        _service.ToggleVote(pollId, "opt-mon", voter1.Id);
        _service.ToggleVote(pollId, "opt-tue", voter1.Id);

        var updated = _polls.GetById(pollId)!;
        Assert.Contains(voter1.Id, updated.Options[0].VoterIds);
        Assert.Contains(voter1.Id, updated.Options[1].VoterIds);
    }

    [Fact]
    public void UpdatePoll_RetainsExistingVoterIds_AndDeduplicatesWhenSwitchingToSingleChoice()
    {
        var (channel, creator, voter1, voter2) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Original Question",
                Options =
                [
                    new() { Id = "opt-1", Text = "Option 1" },
                    new() { Id = "opt-2", Text = "Option 2" },
                    new() { Id = "opt-3", Text = "Option 3" }
                ],
                AllowMultipleAnswers = true
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        // voter1 votes on opt-1 and opt-2; voter2 votes on opt-2
        _service.ToggleVote(pollId, "opt-1", voter1.Id);
        _service.ToggleVote(pollId, "opt-2", voter1.Id);
        _service.ToggleVote(pollId, "opt-2", voter2.Id);

        // Edit poll: rename opt-1, keep opt-2, remove opt-3, add opt-4, and switch to single-choice
        var updateRes = _service.UpdatePoll(
            new ChatPoll
            {
                Id = pollId,
                ChannelId = channel.Id,
                Question = "Updated Question",
                Options =
                [
                    new() { Id = "opt-1", Text = "Option 1 Renamed", Emoji = "🔥" },
                    new() { Id = "opt-2", Text = "Option 2" },
                    new() { Id = "", Text = "Brand New Option 4" }
                ],
                AllowMultipleAnswers = false,
                AllowAddOptions = true
            },
            creator.Id);

        Assert.True(updateRes.IsSuccess);
        var updated = _polls.GetById(pollId)!;
        Assert.Equal("Updated Question", updated.Question);
        Assert.Equal(3, updated.Options.Count);
        Assert.Equal("Option 1 Renamed", updated.Options[0].Text);
        Assert.Equal("🔥", updated.Options[0].Emoji);

        // voter1 voted on opt-1 and opt-2, but since allowMultipleAnswers became false, voter1 keeps their first vote (opt-1) and is removed from opt-2
        Assert.Contains(voter1.Id, updated.Options[0].VoterIds);
        Assert.DoesNotContain(voter1.Id, updated.Options[1].VoterIds);
        // voter2 only voted on opt-2 so they remain on opt-2
        Assert.Contains(voter2.Id, updated.Options[1].VoterIds);
    }

    [Fact]
    public void AddParticipantOption_WhenAllowed_AddsOptionAndAutoVotes()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Movie night?",
                Options =
                [
                    new() { Id = "opt-1", Text = "Interstellar" },
                    new() { Id = "opt-2", Text = "Arrival" }
                ],
                AllowMultipleAnswers = false,
                AllowAddOptions = true
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        var addRes = _service.AddParticipantOption(pollId, voter1.Id, "Dune", "🏜️");
        Assert.True(addRes.IsSuccess);

        var updated = _polls.GetById(pollId)!;
        Assert.Equal(3, updated.Options.Count);
        var addedOpt = updated.Options[2];
        Assert.Equal("Dune", addedOpt.Text);
        Assert.Equal("🏜️", addedOpt.Emoji);
        Assert.Equal(voter1.Id, addedOpt.AddedByUserId);
        Assert.Contains(voter1.Id, addedOpt.VoterIds);
    }

    [Fact]
    public void SetPollClosed_PreventsSubsequentVotes()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Quick vote",
                Options =
                [
                    new() { Id = "opt-1", Text = "Yes" },
                    new() { Id = "opt-2", Text = "No" }
                ]
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        var closeRes = _service.SetPollClosed(pollId, creator.Id, isClosed: true);
        Assert.True(closeRes.IsSuccess);
        Assert.True(_polls.GetById(pollId)!.IsClosed);

        var voteRes = _service.ToggleVote(pollId, "opt-1", voter1.Id);
        Assert.False(voteRes.IsSuccess);
        Assert.Equal("POLL_CLOSED", voteRes.Code);
    }

    [Fact]
    public void DeletePoll_EnforcesPermissionsAndDeletesFromRepository()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Delete test",
                Options =
                [
                    new() { Id = "opt-1", Text = "A" },
                    new() { Id = "opt-2", Text = "B" }
                ]
            },
            creator.Id);

        var pollId = createRes.Value!.Id;

        // Non-creator without moderator rights cannot delete
        var forbiddenRes = _service.DeletePoll(pollId, voter1.Id);
        Assert.False(forbiddenRes.IsSuccess);
        Assert.Equal("FORBIDDEN", forbiddenRes.Code);
        Assert.NotNull(_polls.GetById(pollId));

        // Creator can delete
        var okRes = _service.DeletePoll(pollId, creator.Id);
        Assert.True(okRes.IsSuccess);
        Assert.Null(_polls.GetById(pollId));
    }

    [Fact]
    public async Task AnonymousPoll_NeverRecordsVoterIdsInRamOrOnDisk_AndCannotDisableAnonymous()
    {
        var (channel, creator, voter1, voter2) = SeedBasicChannelAndUsers();
        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Secret ballot?",
                IsAnonymous = true,
                AllowAddOptions = true,
                Options =
                [
                    new() { Id = "opt-1", Text = "Option A" },
                    new() { Id = "opt-2", Text = "Option B" }
                ]
            },
            creator.Id);

        Assert.True(createRes.IsSuccess);
        var pollId = createRes.Value!.Id;

        _service.ToggleVote(pollId, "opt-1", voter1.Id);
        _service.ToggleVote(pollId, "opt-1", voter2.Id);
        _service.AddParticipantOption(pollId, voter1.Id, "Option C");

        var inRam = _polls.GetById(pollId)!;
        Assert.Equal(3, inRam.TotalVotes);
        Assert.Equal(2, inRam.Options[0].VoteCount);
        Assert.Equal(0, inRam.Options[1].VoteCount);
        Assert.Equal(1, inRam.Options[2].VoteCount);

        // Verify zero voter IDs or added-by user IDs exist in RAM
        Assert.All(inRam.Options, opt =>
        {
            Assert.Empty(opt.VoterIds);
            Assert.Null(opt.AddedByUserId);
        });

        // Flush persistence and verify the JSON on disk does not contain voter1 or voter2 IDs
        await _persistence.FlushAllAsync();
        var jsonPath = Path.Combine(_testDataDir, "Polls", $"{pollId}.json");
        var rawJson = File.ReadAllText(jsonPath);
        Assert.DoesNotContain(voter1.Id, rawJson);
        Assert.DoesNotContain(voter2.Id, rawJson);

        // Verify anonymous toggle cannot be turned off via UpdatePoll
        var disableAnonAttempt = _service.UpdatePoll(
            new ChatPoll
            {
                Id = pollId,
                ChannelId = channel.Id,
                Question = "Secret ballot?",
                IsAnonymous = false,
                Options = inRam.Options
            },
            creator.Id);

        Assert.False(disableAnonAttempt.IsSuccess);
        Assert.Equal("INVALID_OPERATION", disableAnonAttempt.Code);
        Assert.True(_polls.GetById(pollId)!.IsAnonymous);
    }

    [Fact]
    public void ExtractMessageTokens_AndPreviewFormatter_HandlePollMentionTag()
    {
        var parsed = ChatService.ExtractMessageTokens("Vote here: #[Friday Lunch](poll:poll-abc-123)");
        Assert.Contains("poll-abc-123", parsed.MentionedPollIds);

        var preview = ChatPreviewFormatter.FormatPreviewContent("#[Where to eat?](poll:321)");
        Assert.Equal("📊 Where to eat?", preview);
    }

    [Fact]
    public void GetPollById_And_GetPollsForChannel_EnforceChannelAuthorizationAndExpectedChannelMatch()
    {
        var member = new Employee { Id = "poll-member", FirstName = "Member", LastName = "One" };
        var intruder = new Employee { Id = "poll-intruder", FirstName = "Intruder", LastName = "Two" };
        _employees.Save(member);
        _employees.Save(intruder);

        var privateGroup = new ChatChannel
        {
            Id = "grp-poll-secret",
            Name = "Secret Group",
            ChannelType = ChatChannelType.Group,
            CreatedById = member.Id,
            ParticipantIds = [member.Id]
        };
        var publicChan = new ChatChannel
        {
            Id = "pub-poll-chan",
            Name = "Public",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(privateGroup);
        _channels.Save(publicChan);

        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = privateGroup.Id,
                Question = "Confidential roadmap vote?",
                Options =
                [
                    new() { Text = "Plan A" },
                    new() { Text = "Plan B" }
                ]
            },
            member.Id);
        Assert.True(createRes.IsSuccess);
        var pollId = createRes.Value!.Id;

        // Authorized participant with matching expectedChannelId succeeds
        Assert.NotNull(_service.GetPollById(pollId, member.Id, expectedChannelId: privateGroup.Id));
        Assert.Single(_service.GetPollsForChannel(privateGroup.Id, member.Id));

        // Empty user or unauthorized non-participant gets null / empty
        Assert.Null(_service.GetPollById(pollId, ""));
        Assert.Null(_service.GetPollById(pollId, intruder.Id));
        Assert.Empty(_service.GetPollsForChannel(privateGroup.Id, intruder.Id));

        // Cross-channel poll card spoofing (poll from privateGroup embedded in publicChan) returns null even for member!
        Assert.Null(_service.GetPollById(pollId, member.Id, expectedChannelId: publicChan.Id));
        Assert.Null(_service.GetPollById(pollId, intruder.Id, expectedChannelId: publicChan.Id));
    }

    [Fact]
    public void CreatePoll_And_UpdatePoll_ZeroForgedVotesAndEnforceServerSideAddedByUserId()
    {
        var (channel, creator, voter1, voter2) = SeedBasicChannelAndUsers();

        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Rigged election?",
                Options =
                [
                    new()
                    {
                        Id = "opt-a",
                        Text = "Option A",
                        VoterIds = [voter1.Id, voter2.Id, "fake-user"],
                        AnonymousVoteCount = 999,
                        AddedByUserId = voter2.Id
                    },
                    new()
                    {
                        Id = "opt-b",
                        Text = "Option B",
                        VoterIds = ["another-fake"],
                        AnonymousVoteCount = 42,
                        AddedByUserId = "spoofed-author"
                    }
                ]
            },
            creator.Id);

        Assert.True(createRes.IsSuccess);
        var created = _polls.GetById(createRes.Value!.Id)!;
        Assert.All(created.Options, opt =>
        {
            Assert.Empty(opt.VoterIds);
            Assert.Equal(0, opt.AnonymousVoteCount);
            Assert.Equal(creator.Id, opt.AddedByUserId);
        });

        // Cast one legitimate vote on opt-a
        Assert.True(_service.ToggleVote(created.Id, "opt-a", voter1.Id).IsSuccess);

        // Attempt to stuff votes on both existing opt-a and a newly added opt-c during UpdatePoll
        var updateRes = _service.UpdatePoll(
            new ChatPoll
            {
                Id = created.Id,
                ChannelId = channel.Id,
                Question = "Rigged election?",
                Options =
                [
                    new()
                    {
                        Id = "opt-a",
                        Text = "Option A",
                        VoterIds = [voter1.Id, voter2.Id, "forged-voter"],
                        AnonymousVoteCount = 500
                    },
                    new()
                    {
                        Id = "opt-b",
                        Text = "Option B"
                    },
                    new()
                    {
                        Id = "forged-new-id",
                        Text = "Option C (New)",
                        VoterIds = [voter2.Id],
                        AnonymousVoteCount = 123,
                        AddedByUserId = voter2.Id
                    }
                ]
            },
            creator.Id);

        Assert.True(updateRes.IsSuccess);
        var updated = _polls.GetById(created.Id)!;
        // Existing opt-a retains ONLY voter1's legitimate vote
        Assert.Single(updated.Options[0].VoterIds);
        Assert.Contains(voter1.Id, updated.Options[0].VoterIds);
        Assert.Equal(0, updated.Options[0].AnonymousVoteCount);

        // Brand-new Option C starts with 0 votes and AddedByUserId = creator.Id
        Assert.Empty(updated.Options[2].VoterIds);
        Assert.Equal(0, updated.Options[2].AnonymousVoteCount);
        Assert.Equal(creator.Id, updated.Options[2].AddedByUserId);
    }

    [Fact]
    public void EncryptedChannelPolls_RequirePlainKeyAndChannelKeyMembership_OnCreateUpdateAndAddOption()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers(isEncrypted: true);
        var outsiderWithPublicAccess = new Employee
        {
            Id = "emp-no-enc-key",
            FirstName = "NoKey",
            LastName = "User",
            EncryptedPrivateKey = "has-personal-vault"
        };
        _employees.Save(outsiderWithPublicAccess);

        var plainKey = _crypto.GenerateAesKeyBase64();

        // Missing plainChannelKey fails with VAULT_LOCKED
        var missingKeyCreate = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Encrypted?",
                Options = [new() { Text = "Yes" }, new() { Text = "No" }]
            },
            creator.Id,
            plainChannelKey: null);
        Assert.False(missingKeyCreate.IsSuccess);
        Assert.Equal("VAULT_LOCKED", missingKeyCreate.Code);

        // User lacking an entry in channel.EncryptedChannelKeys fails with KEY_NOT_FOUND
        var unkeyedCreate = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Injected key?",
                Options = [new() { Text = "Yes" }, new() { Text = "No" }]
            },
            outsiderWithPublicAccess.Id,
            plainChannelKey: plainKey);
        Assert.False(unkeyedCreate.IsSuccess);
        Assert.Equal("KEY_NOT_FOUND", unkeyedCreate.Code);

        // Authorized creator with key creates poll with AllowAddOptions = true
        var validCreate = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Valid Encrypted Poll",
                AllowAddOptions = true,
                Options = [new() { Text = "A" }, new() { Text = "B" }]
            },
            creator.Id,
            plainChannelKey: plainKey);
        Assert.True(validCreate.IsSuccess);
        var poll = validCreate.Value!;

        // AddParticipantOption without plainKey or without EncryptedChannelKeys entry is rejected
        Assert.Equal("VAULT_LOCKED", _service.AddParticipantOption(poll.Id, voter1.Id, "C", plainChannelKey: null).Code);
        Assert.Equal("KEY_NOT_FOUND", _service.AddParticipantOption(poll.Id, outsiderWithPublicAccess.Id, "C", plainChannelKey: plainKey).Code);
        Assert.True(_service.AddParticipantOption(poll.Id, voter1.Id, "C", plainChannelKey: plainKey).IsSuccess);
    }

    [Fact]
    public void OrphanedChannelAndSuspendedUsers_FailClosedOnPollMutations_WhileSuspendedCanStillRead()
    {
        var (channel, creator, voter1, _) = SeedBasicChannelAndUsers();
        var suspended = new Employee
        {
            Id = "emp-poll-suspended",
            FirstName = "Suspended",
            LastName = "Reader",
            IsAdmin = true,
            IsSuspended = true
        };
        _employees.Save(suspended);

        var createRes = _service.CreatePoll(
            new ChatPoll
            {
                ChannelId = channel.Id,
                Question = "Read-only for suspended?",
                AllowAddOptions = true,
                Options = [new() { Id = "o1", Text = "Yes" }, new() { Id = "o2", Text = "No" }]
            },
            creator.Id);
        var poll = createRes.Value!;

        // Suspended user can read poll in accessible channel, but cannot create, edit, vote, add option, close, or delete
        Assert.NotNull(_service.GetPollById(poll.Id, suspended.Id, channel.Id));
        Assert.False(_service.CanUserModifyPoll(poll, suspended));
        Assert.False(_service.CreatePoll(new ChatPoll { ChannelId = channel.Id, Question = "Q?", Options = [new() { Text = "1" }, new() { Text = "2" }] }, suspended.Id).IsSuccess);
        Assert.False(_service.UpdatePoll(poll, suspended.Id).IsSuccess);
        Assert.False(_service.ToggleVote(poll.Id, "o1", suspended.Id).IsSuccess);
        Assert.False(_service.AddParticipantOption(poll.Id, suspended.Id, "Maybe").IsSuccess);
        Assert.False(_service.SetPollClosed(poll.Id, suspended.Id, true).IsSuccess);
        Assert.False(_service.DeletePoll(poll.Id, suspended.Id).IsSuccess);

        // If the underlying channel is deleted (orphaned poll), all read/modify/vote/add-option operations fail closed
        _channels.Delete(channel.Id);
        Assert.Null(_service.GetPollById(poll.Id, creator.Id));
        Assert.False(_service.CanUserModifyPoll(poll, creator));
        Assert.False(_service.ToggleVote(poll.Id, "o1", voter1.Id).IsSuccess);
        Assert.False(_service.AddParticipantOption(poll.Id, voter1.Id, "Orphan Opt").IsSuccess);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try
            {
                Directory.Delete(_testDataDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup contention in temp directory
            }
        }
    }
}
