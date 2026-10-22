using System.Collections.Generic;
using Spokes_Server.Components.Pages.Chat;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class MessageBubbleRenderFingerprintTests
{
    [Fact]
    public void BuildRenderFingerprint_WhenChannelKeyChanges_ChangesFingerprint()
    {
        var fingerprintBeforeUnlock = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-123_v1",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        var fingerprintAfterUnlock = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-123_v1",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "",
            decryptedReplyContent: null,
            channelKey: "base64DecryptedChannelKey",
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        Assert.NotEqual(fingerprintBeforeUnlock, fingerprintAfterUnlock);
        Assert.Contains("base64DecryptedChannelKey", fingerprintAfterUnlock);
    }

    [Fact]
    public void BuildRenderFingerprint_WhenDecryptedContentTransitionsFromNullToEmpty_ChangesFingerprint()
    {
        var fingerprintBeforeDecryption = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-empty-caption",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: null,
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        // When a video with no text caption is decrypted, DecryptedContent becomes ""
        var fingerprintAfterDecryption = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-empty-caption",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        Assert.NotEqual(fingerprintBeforeDecryption, fingerprintAfterDecryption);
    }

    [Fact]
    public void BuildRenderFingerprint_WhenDecryptedReplyContentTransitionsFromNullToEmpty_ChangesFingerprint()
    {
        var fingerprintBefore = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-reply",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "text",
            decryptedReplyContent: null,
            channelKey: "key",
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        var fingerprintAfter = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-reply",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "text",
            decryptedReplyContent: "",
            channelKey: "key",
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        Assert.NotEqual(fingerprintBefore, fingerprintAfter);
    }

    [Fact]
    public void BuildRenderFingerprint_WhenNoPropertiesChange_ProducesIdenticalFingerprint()
    {
        var readers = new List<string> { "user-1", "user-2" };

        var fingerprint1 = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-static",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello world",
            decryptedReplyContent: null,
            channelKey: "test-key-123",
            readReceiptUserIds: readers,
            linkPreviewsCount: 1,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        var fingerprint2 = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-static",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello world",
            decryptedReplyContent: null,
            channelKey: "test-key-123",
            readReceiptUserIds: readers,
            linkPreviewsCount: 1,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null);

        Assert.Equal(fingerprint1, fingerprint2);
    }

    [Fact]
    public void BuildRenderFingerprint_WhenIsMobileChanges_ChangesFingerprint()
    {
        var desktopFingerprint = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-mobile-test",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null,
            isMobile: false);

        var mobileFingerprint = MessageBubble.BuildRenderFingerprint(
            dataFingerprint: "msg-mobile-test",
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: false,
            currentVoiceChannelId: null,
            isMobile: true);

        Assert.NotEqual(desktopFingerprint, mobileFingerprint);
    }
}
