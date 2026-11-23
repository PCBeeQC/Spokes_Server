using System;
using System.IO;
using System.Text;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Storage;

public class ThumbnailCacheServiceTests
{
    [Fact]
    public void TryGet_And_Set_EnforcesLruEvictionOrder()
    {
        var cache = new ThumbnailCacheService(maxTotalBytes: 25, maxEntryBytes: 20);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(cache.Set("a_thumb.jpg", new byte[10], t, "image/jpeg"));
        Assert.True(cache.Set("b_thumb.jpg", new byte[10], t, "image/jpeg"));
        Assert.Equal(2, cache.Count);
        Assert.Equal(20, cache.CurrentBytes);

        // Touch 'a' so 'b' becomes the least-recently-used entry
        Assert.True(cache.TryGet("a_thumb.jpg", out var aEntry));
        Assert.NotNull(aEntry);

        // Adding 'c' (10 bytes -> 30 > 25) must evict 'b', keeping 'a' and 'c'
        Assert.True(cache.Set("c_thumb.jpg", new byte[10], t, "image/jpeg"));
        Assert.Equal(2, cache.Count);
        Assert.Equal(20, cache.CurrentBytes);
        Assert.True(cache.TryGet("a_thumb.jpg", out _));
        Assert.False(cache.TryGet("b_thumb.jpg", out _));
        Assert.True(cache.TryGet("c_thumb.jpg", out _));
    }

    [Fact]
    public void Set_RejectsEntriesExceedingMaxEntryBytes()
    {
        var cache = new ThumbnailCacheService(maxTotalBytes: 1024, maxEntryBytes: 64);
        var oversized = new byte[65];

        Assert.False(cache.Set("huge_thumb.jpg", oversized, DateTimeOffset.UtcNow, "image/jpeg"));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void InvalidateFileAndThumbnails_RemovesBaseAndCompanionVariants()
    {
        var cache = new ThumbnailCacheService();
        var dir = Path.Combine(Path.GetTempPath(), "spokes_thumb_cache_test");
        var origPath = Path.Combine(dir, "photo.png");
        var stdThumb = Path.Combine(dir, "photo.png_thumb.jpg");
        var smThumb = Path.Combine(dir, "photo.png_thumb_sm.jpg");
        var legacyThumb = Path.Combine(dir, "photo_thumb.jpg");
        var unrelatedThumb = Path.Combine(dir, "other.png_thumb.jpg");
        var payload = Encoding.UTF8.GetBytes("jpeg-bytes");
        var now = DateTimeOffset.UtcNow;

        cache.Set(origPath, payload, now, "image/png");
        cache.Set(stdThumb, payload, now, "image/jpeg");
        cache.Set(smThumb, payload, now, "image/jpeg");
        cache.Set(legacyThumb, payload, now, "image/jpeg");
        cache.Set(unrelatedThumb, payload, now, "image/jpeg");

        cache.InvalidateFileAndThumbnails(origPath);

        Assert.False(cache.TryGet(origPath, out _));
        Assert.False(cache.TryGet(stdThumb, out _));
        Assert.False(cache.TryGet(smThumb, out _));
        Assert.False(cache.TryGet(legacyThumb, out _));
        Assert.True(cache.TryGet(unrelatedThumb, out _));
    }

    [Fact]
    public void TrySetIfUnchanged_PreventsStaleFillAfterInvalidation()
    {
        var cache = new ThumbnailCacheService();
        var path = Path.Combine(Path.GetTempPath(), "spokes_epoch_thumb.jpg");
        var payload = Encoding.UTF8.GetBytes("stale-bytes");

        long epochBeforeDelete = cache.CaptureEpoch();
        cache.Invalidate(path);

        bool stored = cache.TrySetIfUnchanged(path, payload, DateTimeOffset.UtcNow, "image/jpeg", epochBeforeDelete);
        Assert.False(stored);
        Assert.False(cache.TryGet(path, out _));

        long freshEpoch = cache.CaptureEpoch();
        Assert.True(cache.TrySetIfUnchanged(path, payload, DateTimeOffset.UtcNow, "image/jpeg", freshEpoch));
        Assert.True(cache.TryGet(path, out var entry));
        Assert.Equal(payload, entry!.RawBytes);
    }

    [Fact]
    public void DefaultMaxEntryBytes_AllowsOneMegabyteThumbnails()
    {
        var cache = new ThumbnailCacheService();
        Assert.Equal(1024 * 1024, cache.MaxEntryBytes);
        var largeThumb = new byte[700 * 1024];
        Assert.True(cache.Set("large_thumb.jpg", largeThumb, DateTimeOffset.UtcNow, "image/jpeg"));
        Assert.True(cache.TryGet("large_thumb.jpg", out var cached));
        Assert.Equal(largeThumb.Length, cached!.RawBytes.Length);
    }
}
