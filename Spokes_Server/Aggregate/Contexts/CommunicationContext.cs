namespace Spokes_Server.Aggregate.Contexts;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;

public class CommunicationContext
{
    private readonly CompanyProfileRepository? _companyProfile;

    public virtual ChatChannelRepository ChatChannels { get; }
    public virtual ChatCategoryRepository ChatCategories { get; }
    public virtual ChatMessageRepository ChatMessages { get; }
    public virtual ChatReadStateRepository ChatReadStates { get; }
    public virtual UserClientStateRepository ClientStates { get; }
    public virtual ReportedMessageRepository ReportedMessages { get; }
    public virtual AlbumRepository Albums { get; }
    public virtual EmailFolderRepository EmailFolders { get; }
    public virtual EmailMessageRepository EmailMessages { get; }
    public virtual PublicContactRepository PublicContacts { get; }
    public virtual PrivateContactRepository PrivateContacts { get; }

    protected CommunicationContext()
    {
        ChatChannels = null!;
        ChatCategories = null!;
        ChatMessages = null!;
        ChatReadStates = null!;
        ClientStates = null!;
        ReportedMessages = null!;
        Albums = null!;
        EmailFolders = null!;
        EmailMessages = null!;
        PublicContacts = null!;
        PrivateContacts = null!;
    }

    [ActivatorUtilitiesConstructor]
    public CommunicationContext(
        ChatChannelRepository chatChannels,
        ChatCategoryRepository chatCategories,
        ChatMessageRepository chatMessages,
        ChatReadStateRepository chatReadStates,
        UserClientStateRepository clientStates,
        ReportedMessageRepository reportedMessages,
        AlbumRepository albums,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        PublicContactRepository publicContacts,
        PrivateContactRepository privateContacts,
        CompanyProfileRepository? companyProfile = null)
    {
        ChatChannels = chatChannels;
        ChatCategories = chatCategories;
        ChatMessages = chatMessages;
        ChatReadStates = chatReadStates;
        ClientStates = clientStates;
        ReportedMessages = reportedMessages;
        Albums = albums;
        EmailFolders = emailFolders;
        EmailMessages = emailMessages;
        PublicContacts = publicContacts;
        PrivateContacts = privateContacts;
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
            () => ClientStates.LoadFromDisk(),
            () => ReportedMessages.LoadFromDisk(),
            () => Albums.LoadFromDisk(),
            () => EmailFolders.LoadFromDisk(),
            () => EmailMessages.LoadFromDisk(),
            () => PublicContacts.LoadFromDisk(),
            () => PrivateContacts.LoadFromDisk()
        );

        ChatChannels.GetOrCreateDefaultGeneral();
        ChatCategories.EnsureDefaultCategories();
        ChatChannels.ReconcileChannelActivityTimestamps(ChatMessages);
    }
}
