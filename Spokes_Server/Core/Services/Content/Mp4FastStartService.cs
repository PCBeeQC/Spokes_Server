namespace Spokes_Server.Core.Services.Content;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

public record VideoMetadataResult(
    bool Success,
    bool WasModified,
    int? Width,
    int? Height,
    double? DurationSeconds,
    bool IsPortrait,
    Stream? ProcessedStream = null);

public interface IMp4FastStartService
{
    bool TryProcessMp4(Stream inputStream, out VideoMetadataResult result);
}

/// <summary>
/// High-performance, pure C# service for MP4/MOV fast-start optimization and metadata extraction.
/// Relocates the 'moov' atom to the front of the file (before 'mdat') by adjusting chunk offsets,
/// enabling instant playback without seeking to EOF.
/// Also extracts video dimensions (accounting for orientation matrix) and duration.
/// </summary>
public class Mp4FastStartService : IMp4FastStartService
{
    private readonly ILogger<Mp4FastStartService>? _logger;

    public Mp4FastStartService(ILogger<Mp4FastStartService>? logger = null)
    {
        _logger = logger;
    }

    private record BoxHeader(string Type, long Offset, int HeaderSize, long TotalSize);

    public bool TryProcessMp4(Stream inputStream, out VideoMetadataResult result)
    {
        result = new VideoMetadataResult(false, false, null, null, null, false, null);

        if (inputStream == null || !inputStream.CanRead)
        {
            return false;
        }

        Stream seekableStream = inputStream;
        bool isTempStream = false;

        try
        {
            if (!inputStream.CanSeek)
            {
                var memoryOrTemp = new FileStream(
                    Path.Combine(Path.GetTempPath(), $"spokes_faststart_{Guid.NewGuid():N}.tmp"),
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    65536,
                    FileOptions.DeleteOnClose);
                inputStream.CopyTo(memoryOrTemp);
                memoryOrTemp.Position = 0;
                seekableStream = memoryOrTemp;
                isTempStream = true;
            }

            long streamLength = seekableStream.Length;
            if (streamLength < 16)
            {
                return false;
            }

            seekableStream.Position = 0;

            // 1. Scan top-level boxes
            var boxes = new List<BoxHeader>();
            BoxHeader? ftypBox = null;
            BoxHeader? moovBox = null;
            BoxHeader? mdatBox = null;

            while (seekableStream.Position + 8 <= streamLength)
            {
                long boxStart = seekableStream.Position;
                var headerBuf = new byte[8];
                int read = seekableStream.Read(headerBuf, 0, 8);
                if (read < 8) break;

                uint size32 = BinaryPrimitives.ReadUInt32BigEndian(headerBuf.AsSpan(0, 4));
                string type = Encoding.ASCII.GetString(headerBuf, 4, 4);

                int headerSize = 8;
                long totalSize = size32;

                if (size32 == 1)
                {
                    // Extended 64-bit size
                    var extBuf = new byte[8];
                    int extRead = seekableStream.Read(extBuf, 0, 8);
                    if (extRead < 8) break;
                    headerSize = 16;
                    totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extBuf);
                }
                else if (size32 == 0)
                {
                    // Extends to EOF
                    totalSize = streamLength - boxStart;
                }

                if (totalSize < headerSize)
                {
                    // Malformed box
                    break;
                }

                var box = new BoxHeader(type, boxStart, headerSize, totalSize);
                boxes.Add(box);

                if (type == "ftyp" && ftypBox == null) ftypBox = box;
                else if (type == "moov" && moovBox == null) moovBox = box;
                else if (type == "mdat" && mdatBox == null) mdatBox = box;

                long nextPos = boxStart + totalSize;
                if (nextPos <= boxStart || nextPos > streamLength)
                {
                    break;
                }
                seekableStream.Position = nextPos;
            }

            // Not an ISO BMFF / MP4 if moov is missing
            if (moovBox == null)
            {
                return false;
            }

            // 2. Read moov box bytes
            if (moovBox.TotalSize > 50 * 1024 * 1024) // Sanity limit 50MB for moov metadata
            {
                _logger?.LogWarning("moov atom exceeds 50MB sanity limit ({Size} bytes), skipping relocation", moovBox.TotalSize);
                return false;
            }

            byte[] moovBytes = new byte[moovBox.TotalSize];
            seekableStream.Position = moovBox.Offset;
            int moovRead = 0;
            while (moovRead < moovBox.TotalSize)
            {
                int r = seekableStream.Read(moovBytes, moovRead, (int)moovBox.TotalSize - moovRead);
                if (r == 0) break;
                moovRead += r;
            }

            if (moovRead < moovBox.TotalSize)
            {
                return false;
            }

            // 3. Extract metadata from moov (duration, width, height, orientation)
            ExtractMoovMetadata(moovBytes, moovBox.HeaderSize, out int? width, out int? height, out double? durationSeconds, out bool isPortrait);

            // 4. If moov is already before mdat (or mdat is missing), file is already FastStart
            if (mdatBox == null || moovBox.Offset < mdatBox.Offset)
            {
                seekableStream.Position = 0;
                result = new VideoMetadataResult(
                    Success: true,
                    WasModified: false,
                    Width: width,
                    Height: height,
                    DurationSeconds: durationSeconds,
                    IsPortrait: isPortrait,
                    ProcessedStream: isTempStream ? seekableStream : inputStream);
                return true;
            }

            // 5. moov is AFTER mdat. Relocate moov to before mdat!
            // Adjust stco (32-bit) and co64 (64-bit) chunk offsets in moov by moov.TotalSize
            long shiftOffset = moovBox.TotalSize;
            AdjustChunkOffsets(moovBytes, shiftOffset);

            // 6. Write new fast-start stream:
            // [Boxes before mdat] -> [relocated moov] -> [mdat and subsequent boxes excluding original moov]
            var outputStream = new FileStream(
                Path.Combine(Path.GetTempPath(), $"spokes_faststart_{Guid.NewGuid():N}.mp4"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                65536,
                FileOptions.DeleteOnClose);

            // Write all boxes prior to mdat (e.g. ftyp)
            seekableStream.Position = 0;
            CopyByteRange(seekableStream, outputStream, 0, mdatBox.Offset);

            // Write relocated moov box
            outputStream.Write(moovBytes, 0, moovBytes.Length);

            // Write mdat box and everything between mdat and moov
            seekableStream.Position = mdatBox.Offset;
            CopyByteRange(seekableStream, outputStream, mdatBox.Offset, moovBox.Offset - mdatBox.Offset);

            // Write anything after original moov box
            long postMoovOffset = moovBox.Offset + moovBox.TotalSize;
            if (postMoovOffset < streamLength)
            {
                seekableStream.Position = postMoovOffset;
                CopyByteRange(seekableStream, outputStream, postMoovOffset, streamLength - postMoovOffset);
            }

            outputStream.Position = 0;

            if (isTempStream)
            {
                seekableStream.Dispose();
            }

            result = new VideoMetadataResult(
                Success: true,
                WasModified: true,
                Width: width,
                Height: height,
                DurationSeconds: durationSeconds,
                IsPortrait: isPortrait,
                ProcessedStream: outputStream);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fast-start process MP4 stream.");
            if (isTempStream)
            {
                try { seekableStream.Dispose(); } catch { }
            }
            return false;
        }
    }

    private static void CopyByteRange(Stream source, Stream dest, long start, long length)
    {
        source.Position = start;
        byte[] buffer = new byte[65536];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void AdjustChunkOffsets(byte[] moovBytes, long shift)
    {
        var span = moovBytes.AsSpan();
        int pos = 8; // skip moov box header

        while (pos + 8 <= span.Length)
        {
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4));
            string boxType = Encoding.ASCII.GetString(moovBytes, pos + 4, 4);

            int headerSize = 8;
            long totalSize = boxSize;
            if (boxSize == 1 && pos + 16 <= span.Length)
            {
                headerSize = 16;
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(pos + 8, 8));
            }

            if (totalSize < headerSize || pos + totalSize > span.Length)
            {
                break;
            }

            if (boxType is "trak" or "mdia" or "minf" or "stbl")
            {
                // Descend into container box
                AdjustContainerChunkOffsets(span.Slice(pos + headerSize, (int)(totalSize - headerSize)), moovBytes, pos + headerSize, shift);
            }

            pos += (int)totalSize;
        }
    }

    private static void AdjustContainerChunkOffsets(ReadOnlySpan<byte> container, byte[] rootBytes, int containerAbsOffset, long shift)
    {
        int pos = 0;
        while (pos + 8 <= container.Length)
        {
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(container.Slice(pos, 4));
            string boxType = Encoding.ASCII.GetString(rootBytes, containerAbsOffset + pos + 4, 4);

            int headerSize = 8;
            long totalSize = boxSize;
            if (boxSize == 1 && pos + 16 <= container.Length)
            {
                headerSize = 16;
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(container.Slice(pos + 8, 8));
            }

            if (totalSize < headerSize || pos + totalSize > container.Length)
            {
                break;
            }

            int currentAbsPos = containerAbsOffset + pos;

            if (boxType is "trak" or "mdia" or "minf" or "stbl")
            {
                AdjustContainerChunkOffsets(container.Slice(pos + headerSize, (int)(totalSize - headerSize)), rootBytes, currentAbsPos + headerSize, shift);
            }
            else if (boxType == "stco")
            {
                // 32-bit chunk offsets: [4 header][1 version][3 flags][4 entry_count][entries * 4]
                int tablePos = currentAbsPos + headerSize + 4; // skip version & flags
                if (tablePos + 4 <= rootBytes.Length)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(rootBytes.AsSpan(tablePos, 4));
                    tablePos += 4;
                    for (int i = 0; i < count && tablePos + 4 <= rootBytes.Length; i++)
                    {
                        uint offset = BinaryPrimitives.ReadUInt32BigEndian(rootBytes.AsSpan(tablePos, 4));
                        offset += (uint)shift;
                        BinaryPrimitives.WriteUInt32BigEndian(rootBytes.AsSpan(tablePos, 4), offset);
                        tablePos += 4;
                    }
                }
            }
            else if (boxType == "co64")
            {
                // 64-bit chunk offsets: [4 header][1 version][3 flags][4 entry_count][entries * 8]
                int tablePos = currentAbsPos + headerSize + 4; // skip version & flags
                if (tablePos + 4 <= rootBytes.Length)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(rootBytes.AsSpan(tablePos, 4));
                    tablePos += 4;
                    for (int i = 0; i < count && tablePos + 8 <= rootBytes.Length; i++)
                    {
                        ulong offset = BinaryPrimitives.ReadUInt64BigEndian(rootBytes.AsSpan(tablePos, 8));
                        offset += (ulong)shift;
                        BinaryPrimitives.WriteUInt64BigEndian(rootBytes.AsSpan(tablePos, 8), offset);
                        tablePos += 8;
                    }
                }
            }

            pos += (int)totalSize;
        }
    }

    private static void ExtractMoovMetadata(
        byte[] moovBytes,
        int moovHeaderSize,
        out int? width,
        out int? height,
        out double? durationSeconds,
        out bool isPortrait)
    {
        width = null;
        height = null;
        durationSeconds = null;
        isPortrait = false;

        try
        {
            var span = moovBytes.AsSpan(moovHeaderSize);
            int pos = 0;
            uint movieTimescale = 0;

            while (pos + 8 <= span.Length)
            {
                uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4));
                string boxType = Encoding.ASCII.GetString(moovBytes, moovHeaderSize + pos + 4, 4);

                int headerSize = 8;
                long totalSize = boxSize;
                if (boxSize == 1 && pos + 16 <= span.Length)
                {
                    headerSize = 16;
                    totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(pos + 8, 8));
                }

                if (totalSize < headerSize || pos + totalSize > span.Length) break;

                int contentOffset = pos + headerSize;
                int contentLength = (int)(totalSize - headerSize);

                if (boxType == "mvhd" && contentLength >= 20)
                {
                    byte version = span[contentOffset];
                    if (version == 0 && contentLength >= 20)
                    {
                        movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(contentOffset + 12, 4));
                        uint duration = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(contentOffset + 16, 4));
                        if (movieTimescale > 0 && duration > 0 && duration != uint.MaxValue)
                        {
                            durationSeconds = Math.Round((double)duration / movieTimescale, 2);
                        }
                    }
                    else if (version == 1 && contentLength >= 32)
                    {
                        movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(contentOffset + 20, 4));
                        ulong duration = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(contentOffset + 24, 8));
                        if (movieTimescale > 0 && duration > 0 && duration != ulong.MaxValue)
                        {
                            durationSeconds = Math.Round((double)duration / movieTimescale, 2);
                        }
                    }
                }
                else if (boxType == "trak")
                {
                    ExtractTrackMetadata(span.Slice(contentOffset, contentLength), movieTimescale, out int? trackWidth, out int? trackHeight, out double? trackDuration, out bool trackIsPortrait);
                    if (trackWidth.HasValue && trackHeight.HasValue && trackWidth.Value > 0 && trackHeight.Value > 0 && (width == null || height == null))
                    {
                        width = trackWidth;
                        height = trackHeight;
                        isPortrait = trackIsPortrait;
                    }
                    if (trackDuration.HasValue && trackDuration.Value > 0)
                    {
                        if (durationSeconds == null || durationSeconds.Value <= 2.5 || trackDuration.Value > durationSeconds.Value)
                        {
                            durationSeconds = trackDuration.Value;
                        }
                    }
                }

                pos += (int)totalSize;
            }
        }
        catch { }
    }

    private static void ExtractTrackMetadata(
        ReadOnlySpan<byte> trakSpan,
        uint movieTimescale,
        out int? width,
        out int? height,
        out double? trackDurationSeconds,
        out bool isPortrait)
    {
        width = null;
        height = null;
        trackDurationSeconds = null;
        isPortrait = false;

        int pos = 0;
        while (pos + 8 <= trakSpan.Length)
        {
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(trakSpan.Slice(pos, 4));
            string boxType = Encoding.ASCII.GetString(trakSpan.Slice(pos + 4, 4));

            int headerSize = 8;
            long totalSize = boxSize;
            if (boxSize == 1 && pos + 16 <= trakSpan.Length)
            {
                headerSize = 16;
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(trakSpan.Slice(pos + 8, 8));
            }

            if (totalSize < headerSize || pos + totalSize > trakSpan.Length) break;

            int contentOffset = pos + headerSize;
            int contentLength = (int)(totalSize - headerSize);

            if (boxType == "tkhd" && contentLength >= 84)
            {
                byte version = trakSpan[contentOffset];

                // Track duration in movie timescale
                if (movieTimescale > 0)
                {
                    if (version == 0 && contentLength >= 24)
                    {
                        uint tkhdDur = BinaryPrimitives.ReadUInt32BigEndian(trakSpan.Slice(contentOffset + 20, 4));
                        if (tkhdDur > 0 && tkhdDur != uint.MaxValue)
                        {
                            trackDurationSeconds = Math.Round((double)tkhdDur / movieTimescale, 2);
                        }
                    }
                    else if (version == 1 && contentLength >= 36)
                    {
                        ulong tkhdDur = BinaryPrimitives.ReadUInt64BigEndian(trakSpan.Slice(contentOffset + 28, 8));
                        if (tkhdDur > 0 && tkhdDur != ulong.MaxValue)
                        {
                            trackDurationSeconds = Math.Round((double)tkhdDur / movieTimescale, 2);
                        }
                    }
                }

                int matrixOffset = (version == 1) ? contentOffset + 48 : contentOffset + 36;
                int dimOffset = matrixOffset + 36;

                if (dimOffset + 8 <= trakSpan.Length)
                {
                    uint rawWidth = BinaryPrimitives.ReadUInt32BigEndian(trakSpan.Slice(dimOffset, 4)) >> 16;
                    uint rawHeight = BinaryPrimitives.ReadUInt32BigEndian(trakSpan.Slice(dimOffset + 4, 4)) >> 16;

                    if (rawWidth > 0 && rawHeight > 0)
                    {
                        int b = (int)BinaryPrimitives.ReadInt32BigEndian(trakSpan.Slice(matrixOffset + 4, 4));
                        int c = (int)BinaryPrimitives.ReadInt32BigEndian(trakSpan.Slice(matrixOffset + 12, 4));

                        bool isRotated90or270 = (b != 0 || c != 0);

                        int finalWidth = isRotated90or270 ? (int)rawHeight : (int)rawWidth;
                        int finalHeight = isRotated90or270 ? (int)rawWidth : (int)rawHeight;

                        width = finalWidth;
                        height = finalHeight;
                        isPortrait = finalHeight > finalWidth;
                    }
                }
            }
            else if (boxType == "mdia")
            {
                ExtractMdiaMetadata(trakSpan.Slice(contentOffset, contentLength), out double? mdiaDur);
                if (mdiaDur.HasValue && mdiaDur.Value > 0)
                {
                    if (!trackDurationSeconds.HasValue || mdiaDur.Value > trackDurationSeconds.Value)
                    {
                        trackDurationSeconds = mdiaDur;
                    }
                }
            }

            pos += (int)totalSize;
        }
    }

    private static void ExtractMdiaMetadata(ReadOnlySpan<byte> mdiaSpan, out double? durationSeconds)
    {
        durationSeconds = null;
        int pos = 0;
        while (pos + 8 <= mdiaSpan.Length)
        {
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(mdiaSpan.Slice(pos, 4));
            string boxType = Encoding.ASCII.GetString(mdiaSpan.Slice(pos + 4, 4));

            int headerSize = 8;
            long totalSize = boxSize;
            if (boxSize == 1 && pos + 16 <= mdiaSpan.Length)
            {
                headerSize = 16;
                totalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(mdiaSpan.Slice(pos + 8, 8));
            }

            if (totalSize < headerSize || pos + totalSize > mdiaSpan.Length) break;

            if (boxType == "mdhd")
            {
                int contentOffset = pos + headerSize;
                int contentLength = (int)(totalSize - headerSize);
                if (contentLength >= 20)
                {
                    byte version = mdiaSpan[contentOffset];
                    if (version == 0 && contentLength >= 20)
                    {
                        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mdiaSpan.Slice(contentOffset + 12, 4));
                        uint duration = BinaryPrimitives.ReadUInt32BigEndian(mdiaSpan.Slice(contentOffset + 16, 4));
                        if (timescale > 0 && duration > 0 && duration != uint.MaxValue)
                        {
                            durationSeconds = Math.Round((double)duration / timescale, 2);
                            return;
                        }
                    }
                    else if (version == 1 && contentLength >= 32)
                    {
                        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mdiaSpan.Slice(contentOffset + 20, 4));
                        ulong duration = BinaryPrimitives.ReadUInt64BigEndian(mdiaSpan.Slice(contentOffset + 24, 8));
                        if (timescale > 0 && duration > 0 && duration != ulong.MaxValue)
                        {
                            durationSeconds = Math.Round((double)duration / timescale, 2);
                            return;
                        }
                    }
                }
            }

            pos += (int)totalSize;
        }
    }
}
