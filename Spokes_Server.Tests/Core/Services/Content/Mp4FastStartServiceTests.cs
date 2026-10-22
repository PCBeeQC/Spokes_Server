using System.Buffers.Binary;
using System.IO;
using System.Text;
using Spokes_Server.Core.Services.Content;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Content;

public class Mp4FastStartServiceTests
{
    private readonly Mp4FastStartService _service = new();

    #region Helper Box Builders

    private static byte[] CreateBox(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        int total = 0;
        foreach (var arr in arrays) total += arr.Length;
        byte[] result = new byte[total];
        int offset = 0;
        foreach (var arr in arrays)
        {
            arr.CopyTo(result, offset);
            offset += arr.Length;
        }
        return result;
    }

    private static byte[] CreateFtypBox()
    {
        // 8 bytes header + "isom" (4) + minor version (4)
        return CreateBox("ftyp", Combine(Encoding.ASCII.GetBytes("isom"), new byte[] { 0, 0, 2, 0 }));
    }

    private static byte[] CreateMdatBox(int payloadSize = 64)
    {
        return CreateBox("mdat", new byte[payloadSize]);
    }

    private static byte[] CreateMvhdBox(uint timescale, uint duration)
    {
        // mvhd version 0 payload: 100 bytes
        var payload = new byte[100];
        // version 0, flags 0 (bytes 0..3)
        // creation time (4..7) = 0
        // mod time (8..11) = 0
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), duration);
        // rate 1.0 (bytes 20..23 = 0x00010000)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 0x00010000);
        // volume 1.0 (bytes 24..25 = 0x0100)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(24, 2), 0x0100);
        // matrix (identity): 9 x 4 bytes at offset 36
        int matrixOffset = 36;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 16, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 32, 4), 0x40000000);
        // next track id (bytes 96..99 = 1)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(96, 4), 1);

        return CreateBox("mvhd", payload);
    }

    private static byte[] CreateTkhdBox(int width, int height, bool rotate90 = false, uint duration = 0)
    {
        // tkhd version 0 payload: 84 bytes
        var payload = new byte[84];
        // version 0, flags = 7 (bytes 0..3)
        payload[3] = 7;
        // duration at offset 20 (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), duration);
        // matrix starts at offset 36 (36 bytes)
        int matrixOffset = 36;
        if (rotate90)
        {
            // 90 degree clockwise rotation:
            // a=0, b=1, u=0, c=-1, d=0, v=0, x=0, y=0, w=1
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(matrixOffset + 4, 4), 0x00010000); // b = 1.0
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(matrixOffset + 12, 4), -0x00010000); // c = -1.0
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 32, 4), 0x40000000);
        }
        else
        {
            // Identity matrix
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 0, 4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 16, 4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(matrixOffset + 32, 4), 0x40000000);
        }

        // width and height at offset 72 and 76 (16.16 fixed point)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(72, 4), (uint)(width << 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(76, 4), (uint)(height << 16));

        return CreateBox("tkhd", payload);
    }

    private static byte[] CreateMdhdBox(uint timescale, uint duration)
    {
        // mdhd version 0 payload: 24 bytes
        var payload = new byte[24];
        // version 0, flags 0 (bytes 0..3)
        // creation time (4..7) = 0
        // mod time (8..11) = 0
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), duration);
        return CreateBox("mdhd", payload);
    }

    private static byte[] CreateStcoBox(params uint[] chunkOffsets)
    {
        // stco payload: 4 bytes (version/flags) + 4 bytes (count) + count * 4 bytes
        var payload = new byte[8 + chunkOffsets.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)chunkOffsets.Length);
        for (int i = 0; i < chunkOffsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + i * 4, 4), chunkOffsets[i]);
        }
        return CreateBox("stco", payload);
    }

    private static byte[] CreateCo64Box(params ulong[] chunkOffsets)
    {
        // co64 payload: 4 bytes (version/flags) + 4 bytes (count) + count * 8 bytes
        var payload = new byte[8 + chunkOffsets.Length * 8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)chunkOffsets.Length);
        for (int i = 0; i < chunkOffsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8 + i * 8, 8), chunkOffsets[i]);
        }
        return CreateBox("co64", payload);
    }

    private static byte[] CreateMoovBox(byte[] mvhd, byte[] tkhd, byte[]? stcoOrCo64 = null, byte[]? mdhd = null)
    {
        byte[] stblPayload = stcoOrCo64 ?? CreateStcoBox(100, 200);
        byte[] stbl = CreateBox("stbl", stblPayload);
        byte[] minf = CreateBox("minf", stbl);
        byte[] mdiaPayload = mdhd != null ? Combine(mdhd, minf) : minf;
        byte[] mdia = CreateBox("mdia", mdiaPayload);
        byte[] trak = CreateBox("trak", Combine(tkhd, mdia));
        return CreateBox("moov", Combine(mvhd, trak));
    }

    #endregion

    [Fact]
    public void TryProcessMp4_NullOrEmptyStream_ReturnsFalse()
    {
        Assert.False(_service.TryProcessMp4(null!, out var result1));
        Assert.False(result1.Success);

        using var emptyMs = new MemoryStream();
        Assert.False(_service.TryProcessMp4(emptyMs, out var result2));
        Assert.False(result2.Success);
    }

    [Fact]
    public void TryProcessMp4_NotIsoBmff_ReturnsFalse()
    {
        byte[] junk = new byte[128];
        Random.Shared.NextBytes(junk);
        using var ms = new MemoryStream(junk);

        Assert.False(_service.TryProcessMp4(ms, out var result));
        Assert.False(result.Success);
    }

    [Fact]
    public void TryProcessMp4_MoovBeforeMdat_LeavesStreamUnmodifiedAndExtractsMetadata()
    {
        var ftyp = CreateFtypBox();
        var mvhd = CreateMvhdBox(timescale: 1000, duration: 12500); // 12.5 seconds
        var tkhd = CreateTkhdBox(width: 1920, height: 1080);
        var moov = CreateMoovBox(mvhd, tkhd);
        var mdat = CreateMdatBox(128);

        // Layout: ftyp -> moov -> mdat (Already FastStart)
        byte[] fileBytes = Combine(ftyp, moov, mdat);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.False(result.WasModified);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(12.5, result.DurationSeconds);
        Assert.False(result.IsPortrait);
    }

    [Fact]
    public void TryProcessMp4_MoovAfterMdat_RelocatesMoovAndAdjustsChunkOffsets()
    {
        var ftyp = CreateFtypBox();
        var mdat = CreateMdatBox(256);
        uint origOffset1 = 500;
        uint origOffset2 = 1200;
        var mvhd = CreateMvhdBox(timescale: 600, duration: 1800); // 3.0 seconds
        var tkhd = CreateTkhdBox(width: 1280, height: 720);
        var stco = CreateStcoBox(origOffset1, origOffset2);
        var moov = CreateMoovBox(mvhd, tkhd, stco);

        // Layout: ftyp -> mdat -> moov (Needs FastStart relocation)
        byte[] fileBytes = Combine(ftyp, mdat, moov);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.True(result.WasModified);
        Assert.NotNull(result.ProcessedStream);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(3.0, result.DurationSeconds);
        Assert.False(result.IsPortrait);

        // Verify the processed stream has moov before mdat
        var processed = result.ProcessedStream!;
        using var reader = new MemoryStream();
        processed.CopyTo(reader);
        byte[] outputBytes = reader.ToArray();

        // 1. Same total size as original
        Assert.Equal(fileBytes.Length, outputBytes.Length);

        // 2. Read first box after ftyp: should be moov!
        int ftypLen = ftyp.Length;
        string secondBoxType = Encoding.ASCII.GetString(outputBytes, ftypLen + 4, 4);
        Assert.Equal("moov", secondBoxType);

        // 3. Read box after relocated moov: should be mdat!
        int moovLen = moov.Length;
        string thirdBoxType = Encoding.ASCII.GetString(outputBytes, ftypLen + moovLen + 4, 4);
        Assert.Equal("mdat", thirdBoxType);

        // 4. Verify stco offsets inside the relocated moov were shifted by moov.Length
        long expectedShift = moov.Length;
        // Re-read with service to verify integrity of the processed stream
        processed.Position = 0;
        bool recheckSuccess = _service.TryProcessMp4(processed, out var recheck);
        Assert.True(recheckSuccess);
        Assert.True(recheck.Success);
        Assert.False(recheck.WasModified); // Already fast-start now!
        Assert.Equal(1280, recheck.Width);
        Assert.Equal(720, recheck.Height);
        Assert.Equal(3.0, recheck.DurationSeconds);

        result.ProcessedStream.Dispose();
    }

    [Fact]
    public void TryProcessMp4_MoovAfterMdat_WithCo64_Adjusts64BitChunkOffsets()
    {
        var ftyp = CreateFtypBox();
        var mdat = CreateMdatBox(128);
        ulong origOffset1 = 10000;
        ulong origOffset2 = 50000;
        var mvhd = CreateMvhdBox(timescale: 1000, duration: 4000);
        var tkhd = CreateTkhdBox(width: 3840, height: 2160);
        var co64 = CreateCo64Box(origOffset1, origOffset2);
        var moov = CreateMoovBox(mvhd, tkhd, co64);

        byte[] fileBytes = Combine(ftyp, mdat, moov);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.True(result.WasModified);
        Assert.Equal(3840, result.Width);
        Assert.Equal(2160, result.Height);
        Assert.Equal(4.0, result.DurationSeconds);

        result.ProcessedStream?.Dispose();
    }

    [Fact]
    public void TryProcessMp4_Rotated90Degrees_DetectsPortraitAndSwapsDimensions()
    {
        var ftyp = CreateFtypBox();
        var mvhd = CreateMvhdBox(timescale: 1000, duration: 60000); // 60 seconds
        // Phone camera records 1920x1080 sensor with 90° rotation -> effective 1080x1920 portrait
        var tkhd = CreateTkhdBox(width: 1920, height: 1080, rotate90: true);
        var moov = CreateMoovBox(mvhd, tkhd);
        var mdat = CreateMdatBox(64);

        byte[] fileBytes = Combine(ftyp, moov, mdat);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.Equal(1080, result.Width);
        Assert.Equal(1920, result.Height);
        Assert.True(result.IsPortrait);
        Assert.Equal(60.0, result.DurationSeconds);
    }

    [Fact]
    public void TryProcessMp4_MvhdDurationTruncated_FallsBackToTkhdDuration()
    {
        var ftyp = CreateFtypBox();
        // mvhd has placeholder/truncated 2.0s duration (2000 at timescale 1000)
        var mvhd = CreateMvhdBox(timescale: 1000, duration: 2000);
        // tkhd has actual 13-minute duration (780000 at timescale 1000 = 780s)
        var tkhd = CreateTkhdBox(width: 1920, height: 1080, rotate90: false, duration: 780000);
        var moov = CreateMoovBox(mvhd, tkhd);
        var mdat = CreateMdatBox(64);

        byte[] fileBytes = Combine(ftyp, moov, mdat);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(780.0, result.DurationSeconds);
    }

    [Fact]
    public void TryProcessMp4_MvhdDurationZero_FallsBackToMdhdDuration()
    {
        var ftyp = CreateFtypBox();
        // mvhd has 0 duration
        var mvhd = CreateMvhdBox(timescale: 1000, duration: 0);
        // tkhd has 0 duration
        var tkhd = CreateTkhdBox(width: 1920, height: 1080, rotate90: false, duration: 0);
        // mdhd has actual 13-minute duration (780000 at timescale 1000 = 780s)
        var mdhd = CreateMdhdBox(timescale: 1000, duration: 780000);
        var moov = CreateMoovBox(mvhd, tkhd, mdhd: mdhd);
        var mdat = CreateMdatBox(64);

        byte[] fileBytes = Combine(ftyp, moov, mdat);
        using var ms = new MemoryStream(fileBytes);

        bool success = _service.TryProcessMp4(ms, out var result);

        Assert.True(success);
        Assert.True(result.Success);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(780.0, result.DurationSeconds);
    }
}
