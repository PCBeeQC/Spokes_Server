using System;
using System.Text.RegularExpressions;

namespace Spokes_Server.Core.Helpers
{
    public static class ChatPreviewFormatter
    {
        private static readonly Regex GifRegex = new(@"!\[gif\]\([^)]+\)|\[gif\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RichTagRegex = new(@"#\[([^\]]+)\]\(([a-zA-Z0-9_-]+):[^\)]+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string FormatPreviewContent(string? rawContent, System.Collections.Generic.List<Spokes_Server.Core.Models.Communication.ChatAttachment>? attachments = null)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                if (attachments != null && attachments.Count > 0)
                {
                    var firstAttachment = attachments[0];
                    if (firstAttachment.FileName.StartsWith("VoiceMessage_"))
                    {
                        return "🎤 Voice Memo";
                    }
                    if (firstAttachment.ContentType.StartsWith("audio/"))
                    {
                        return "🎵 Sent audio";
                    }
                    if (firstAttachment.ContentType.StartsWith("image/"))
                    {
                        return "📷 Sent an image";
                    }
                    if (firstAttachment.ContentType.StartsWith("video/"))
                    {
                        return "🎞️ Sent a video";
                    }
                    return "📎 Sent a file";
                }
                return "📎 Attachment";
            }

            var formatted = rawContent;

            // 1. Handle GIFs
            formatted = GifRegex.Replace(formatted, "Sent a GIF");

            // 2. Handle Rich Tags: #[Title](type:Id)
            formatted = RichTagRegex.Replace(formatted, match =>
            {
                var title = match.Groups[1].Value;
                var type = match.Groups[2].Value.ToLowerInvariant();

                return type switch
                {
                    "event" => $"📅 {title}",
                    "album" => $"shared {title} album",
                    "project" => $"shared {title} project",
                    "quote" => $"shared {title} quote",
                    "invoice" => $"shared {title} invoice",
                    _ => $"shared {title} {type}"
                };
            });

            return formatted;
        }

        public static string FormatAndTruncatePreview(string? rawContent, System.Collections.Generic.List<Spokes_Server.Core.Models.Communication.ChatAttachment>? attachments = null, int maxLength = 30)
        {
            var formatted = FormatPreviewContent(rawContent, attachments);
            if (string.IsNullOrEmpty(formatted)) return "";
            
            return formatted.Length > maxLength ? formatted.Substring(0, maxLength - 3) + "..." : formatted;
        }
    }
}
