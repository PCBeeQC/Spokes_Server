using System;
using System.Collections.Generic;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class MessageBubbleRenderFingerprintTests
{
    [Fact]
    public void BuildRenderFingerprint_WhenChannelKeyChanges_ChangesFingerprint()
    {
        var fingerprintBeforeUnlock = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 101,
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
            dataVersionHash: 101,
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
            dataVersionHash: 202,
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
            dataVersionHash: 202,
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
            dataVersionHash: 303,
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
            dataVersionHash: 303,
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
            dataVersionHash: 404,
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
            dataVersionHash: 404,
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
            dataVersionHash: 505,
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
            dataVersionHash: 505,
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

    [Fact]
    public void BuildRenderFingerprint_WhenShowMoreMenuChanges_ChangesFingerprint()
    {
        var closedMenu = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 606,
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: null,
            currentVoiceChannelId: null,
            isMobile: false,
            showMoreMenu: false);

        var openMenu = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 606,
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: null,
            currentVoiceChannelId: null,
            isMobile: false,
            showMoreMenu: true);

        Assert.NotEqual(closedMenu, openMenu);
    }

    [Fact]
    public void BuildRenderFingerprint_WhenIsActionSheetTargetChanges_ChangesFingerprint()
    {
        var inactive = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 607,
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: null,
            currentVoiceChannelId: null,
            isMobile: true,
            showMoreMenu: false,
            isVoiceConnecting: null,
            isActionSheetTarget: false);

        var active = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 607,
            isEditing: false,
            isGrouped: false,
            isOwnMessage: true,
            decryptedContent: "Hello",
            decryptedReplyContent: null,
            channelKey: null,
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: null,
            currentVoiceChannelId: null,
            isMobile: true,
            showMoreMenu: false,
            isVoiceConnecting: null,
            isActionSheetTarget: true);

        Assert.NotEqual(inactive, active);
    }

    [Fact]
    public void BuildRenderFingerprint_WithLargeDecryptedContent_RemainsCompact()
    {
        var hugeText = new string('Z', 200_000);
        var fp = MessageBubble.BuildRenderFingerprint(
            dataVersionHash: 707,
            isEditing: false,
            isGrouped: false,
            isOwnMessage: false,
            decryptedContent: hugeText,
            decryptedReplyContent: hugeText,
            channelKey: "key",
            readReceiptUserIds: null,
            linkPreviewsCount: 0,
            skeletonCount: 0,
            showEmojiPicker: false,
            isExpanded: false,
            isInVoiceCall: null,
            currentVoiceChannelId: null);

        Assert.True(fp.Length < 256, $"Expected compact fingerprint, got {fp.Length} chars.");
    }

    [Fact]
    public void ComputeMessageDataVersionHash_ChangesWhenContentReactionsOrEditsChange()
    {
        var msg = new ChatMessage { Id = "m1", Content = "hello" };
        int h1 = Spokes_Server.Components.Pages.Chat.Chat.ComputeMessageDataVersionHash(msg, null);
        int h1Repeat = Spokes_Server.Components.Pages.Chat.Chat.ComputeMessageDataVersionHash(msg, null);
        Assert.Equal(h1, h1Repeat);

        int hDec = Spokes_Server.Components.Pages.Chat.Chat.ComputeMessageDataVersionHash(msg, "decrypted");
        Assert.NotEqual(h1, hDec);

        msg.Reactions.Add("👍:u1");
        int hReact = Spokes_Server.Components.Pages.Chat.Chat.ComputeMessageDataVersionHash(msg, null);
        Assert.NotEqual(h1, hReact);

        msg.EditedAt = DateTime.UtcNow;
        int hEdit = Spokes_Server.Components.Pages.Chat.Chat.ComputeMessageDataVersionHash(msg, null);
        Assert.NotEqual(hReact, hEdit);
    }

    [Fact]
    public void MessageBubble_DoesNotExposeLargeStringWireParameters()
    {
        // Regression guard: Blazor Server's RenderBatchWriter serializes EVERY string component
        // parameter over SignalR into RenderBatch.Strings. Keep these non-string.
        var type = typeof(MessageBubble);
        Assert.Null(type.GetProperty("DataFingerprint"));
        Assert.Null(type.GetProperty("DecryptedContent"));
        Assert.Null(type.GetProperty("DecryptedReplyContent"));
        Assert.Equal(typeof(int), type.GetProperty(nameof(MessageBubble.DataVersionHash))?.PropertyType);
        Assert.Equal(typeof(IReadOnlyDictionary<string, string>), type.GetProperty(nameof(MessageBubble.DecryptedContents))?.PropertyType);
    }

    [Fact]
    public void MessageBubble_DoesNotRetainSupersededPerMessageToolbarHtmlBuilder()
    {
        // Regression guard: desktop hover action toolbars are rendered once as a shared <template>
        // in Chat.razor and moved into hovered rows by Chat.razor.js, so MessageBubble must not
        // serialize a per-message MarkupString toolbar over Blazor SignalR.
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(typeof(MessageBubble).GetMethod("BuildDesktopActionToolbarHtml", flags));
        Assert.Null(typeof(MessageBubble).GetField("StaticDesktopActionToolbarCache", flags));
    }
}
