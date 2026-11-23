namespace Spokes_Server.Aggregate.Contexts;

using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;

public class CommunicationContext
{
    private readonly CompanyProfileRepository? _companyProfile;

    public virtual ChatChannelRepository ChatChannels { get; }
    public virtual ChatCategoryRepository ChatCategories { get; }
    public virtual ChatMessageRepository ChatMessages { get; }
    public virtual ChatReadStateRepository ChatReadStates { get; }
    public virtual ReportedMessageRepository ReportedMessages { get; }
    public virtual AlbumRepository Albums { get; }
    public virtual EmailAccountRepository EmailAccounts { get; }
    public virtual EmailInboxRepository EmailInboxes { get; }
    public virtual EmailSendingIdentityRepository EmailSendingIdentities { get; }
    public virtual EmailFolderRepository EmailFolders { get; }
    public virtual EmailMessageRepository EmailMessages { get; }
    public virtual PublicContactRepository PublicContacts { get; }
    public virtual PrivateContactRepository PrivateContacts { get; }
    public virtual PhysicalVoiceRoomRepository PhysicalVoiceRooms { get; }
    public virtual ChatPollRepository ChatPolls { get; }

    protected CommunicationContext()
    {
        ChatChannels = null!;
        ChatCategories = null!;
        ChatMessages = null!;
        ChatReadStates = null!;
        ReportedMessages = null!;
        Albums = null!;
        EmailAccounts = null!;
        EmailInboxes = null!;
        EmailSendingIdentities = null!;
        EmailFolders = null!;
        EmailMessages = null!;
        PublicContacts = null!;
        PrivateContacts = null!;
        PhysicalVoiceRooms = null!;
        ChatPolls = null!;
    }

    [ActivatorUtilitiesConstructor]
    public CommunicationContext(
        ChatChannelRepository chatChannels,
        ChatCategoryRepository chatCategories,
        ChatMessageRepository chatMessages,
        ChatReadStateRepository chatReadStates,
        ReportedMessageRepository reportedMessages,
        AlbumRepository albums,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        PublicContactRepository publicContacts,
        PrivateContactRepository privateContacts,
        CompanyProfileRepository? companyProfile = null,
        EmailAccountRepository? emailAccounts = null,
        EmailInboxRepository? emailInboxes = null,
        EmailSendingIdentityRepository? emailSendingIdentities = null,
        PhysicalVoiceRoomRepository? physicalVoiceRooms = null,
        ChatPollRepository? chatPolls = null)
    {
        ChatChannels = chatChannels;
        ChatCategories = chatCategories;
        ChatMessages = chatMessages;
        ChatReadStates = chatReadStates;
        ReportedMessages = reportedMessages;
        Albums = albums;
        EmailAccounts = emailAccounts!;
        EmailInboxes = emailInboxes!;
        EmailSendingIdentities = emailSendingIdentities!;
        EmailFolders = emailFolders;
        EmailMessages = emailMessages;
        PublicContacts = publicContacts;
        PrivateContacts = privateContacts;
        PhysicalVoiceRooms = physicalVoiceRooms!;
        ChatPolls = chatPolls!;
        _companyProfile = companyProfile;
    }

    /// <summary>
    /// Exposes domain-owned chat configuration decomposed from the global company profile.
    /// </summary>
    public virtual ChatConfig GetChatConfig() => _companyProfile?.GetChat() ?? new ChatConfig();

    /// <summary>
    /// Exposes domain-owned push notification configuration decomposed from the global company profile.
    /// </summary>
    public virtual PushConfig GetPushConfig() => _companyProfile?.GetPush() ?? new PushConfig();

    /// <summary>
    /// Exposes domain-owned email server settings decomposed from the global company profile.
    /// </summary>
    public virtual EmailServerSettings GetEmailSettings() => _companyProfile?.GetEmailSettings() ?? new EmailServerSettings();

    public virtual ChatConfig ChatConfig => GetChatConfig();
    public virtual PushConfig PushConfig => GetPushConfig();
    public virtual EmailServerSettings EmailSettings => GetEmailSettings();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => ChatChannels.LoadFromDisk(),
            () => ChatCategories.LoadFromDisk(),
            () => ChatMessages.LoadFromDisk(),
            () => ChatReadStates.LoadFromDisk(),
            () => ReportedMessages.LoadFromDisk(),
            () => Albums.LoadFromDisk(),
            () => EmailAccounts?.LoadFromDisk(),
            () => EmailInboxes?.LoadFromDisk(),
            () => EmailSendingIdentities?.LoadFromDisk(),
            () => EmailFolders.LoadFromDisk(),
            () => EmailMessages.LoadFromDisk(),
            () => PublicContacts.LoadFromDisk(),
            () => PrivateContacts.LoadFromDisk(),
            () => PhysicalVoiceRooms?.LoadFromDisk(),
            () => ChatPolls?.LoadFromDisk()
        );

        ChatChannels.GetOrCreateDefaultGeneral();
        ChatCategories.EnsureDefaultCategories();
        ChatChannels.ReconcileChannelActivityTimestamps(ChatMessages);
    }
}
