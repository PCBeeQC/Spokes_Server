using SkiaSharp;

namespace Spokes_Server.Tests.Core.Services.Content;

public class ImageProcessingServiceTests
{
    private readonly ImageProcessingService _service;

    public ImageProcessingServiceTests()
    {
        _service = new ImageProcessingService();
    }

    private static byte[] CreateTestImageBytes(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static MemoryStream CreateTestImageStream(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        return new MemoryStream(CreateTestImageBytes(width, height, format));
    }

    private class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get; set; } = 0;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Stream read failed");
        public override int Read(Span<byte> buffer) => throw new IOException("Stream read failed");
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void TryGenerateThumbnail_WithValidImage_GeneratesThumbnail()
    {
        using var stream = CreateTestImageStream(200, 200);

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.True(result);
        Assert.NotNull(thumbStream);
        using (thumbStream)
        {
            using var decoded = SKBitmap.Decode(thumbStream);
            Assert.NotNull(decoded);
            Assert.Equal(200, decoded.Width);
            Assert.Equal(200, decoded.Height);
        }
    }

    [Fact]
    public void TryGenerateThumbnail_WithLargeImage_ScalesDownToMaxDim()
    {
        using var stream = CreateTestImageStream(2000, 1000);

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.True(result);
        Assert.NotNull(thumbStream);
        using (thumbStream)
        {
            using var decoded = SKBitmap.Decode(thumbStream);
            Assert.NotNull(decoded);
            Assert.Equal(1280, decoded.Width);
            Assert.Equal(640, decoded.Height);
        }
    }

    [Fact]
    public void TryGenerateThumbnail_WithPortraitLargeImage_ScalesDownToMaxDim()
    {
        using var stream = CreateTestImageStream(1000, 2000);

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.True(result);
        Assert.NotNull(thumbStream);
        using (thumbStream)
        {
            using var decoded = SKBitmap.Decode(thumbStream);
            Assert.NotNull(decoded);
            Assert.Equal(640, decoded.Width);
            Assert.Equal(1280, decoded.Height);
        }
    }

    [Fact]
    public void TryGenerateThumbnail_WithInvalidData_ReturnsFalse()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.False(result);
        Assert.Null(thumbStream);
    }

    [Fact]
    public void TryGenerateThumbnail_WithCorruptedStream_ReturnsFalse()
    {
        using var stream = new ThrowingStream();

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.False(result);
        Assert.Null(thumbStream);
    }

    [Fact]
    public void TryGenerateThumbnail_WithExcessiveDimensions_ReturnsFalse()
    {
        using var stream = CreateTestImageStream(16385, 1);

        var result = _service.TryGenerateThumbnail(stream, out var thumbStream);

        Assert.False(result);
        Assert.Null(thumbStream);
    }

    [Fact]
    public void OptimizeImage_WithValidPngBelowMaxDimension_ReturnsPng()
    {
        var inputBytes = CreateTestImageBytes(100, 100, SKEncodedImageFormat.Png);

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 500);

        Assert.Equal("image/png", mimeType);
        Assert.NotNull(resultBytes);
        using var decoded = SKBitmap.Decode(resultBytes);
        Assert.NotNull(decoded);
        Assert.Equal(100, decoded.Width);
        Assert.Equal(100, decoded.Height);
    }

    [Fact]
    public void OptimizeImage_WithValidJpegAboveMaxDimension_ResizesAndReturnsJpeg()
    {
        var inputBytes = CreateTestImageBytes(1000, 500, SKEncodedImageFormat.Jpeg);

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 500);

        Assert.Equal("image/jpeg", mimeType);
        Assert.NotNull(resultBytes);
        using var decoded = SKBitmap.Decode(resultBytes);
        Assert.NotNull(decoded);
        Assert.Equal(500, decoded.Width);
        Assert.Equal(250, decoded.Height);
    }

    [Fact]
    public void OptimizeImage_WithPortraitAboveMaxDimension_ResizesPreservingAspect()
    {
        var inputBytes = CreateTestImageBytes(500, 1000, SKEncodedImageFormat.Jpeg);

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 500);

        Assert.Equal("image/jpeg", mimeType);
        Assert.NotNull(resultBytes);
        using var decoded = SKBitmap.Decode(resultBytes);
        Assert.NotNull(decoded);
        Assert.Equal(250, decoded.Width);
        Assert.Equal(500, decoded.Height);
    }

    [Fact]
    public void OptimizeImage_WithValidPngAboveMaxDimension_ResizesAndReturnsPng()
    {
        var inputBytes = CreateTestImageBytes(800, 400, SKEncodedImageFormat.Png);

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 400);

        Assert.Equal("image/png", mimeType);
        Assert.NotNull(resultBytes);
        using var decoded = SKBitmap.Decode(resultBytes);
        Assert.NotNull(decoded);
        Assert.Equal(400, decoded.Width);
        Assert.Equal(200, decoded.Height);
    }

    [Fact]
    public void OptimizeImage_WithValidJpegBelowMaxDimension_ReturnsJpeg()
    {
        var inputBytes = CreateTestImageBytes(200, 200, SKEncodedImageFormat.Jpeg);

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 500);

        Assert.Equal("image/jpeg", mimeType);
        Assert.NotNull(resultBytes);
        using var decoded = SKBitmap.Decode(resultBytes);
        Assert.NotNull(decoded);
        Assert.Equal(200, decoded.Width);
        Assert.Equal(200, decoded.Height);
    }

    [Fact]
    public void OptimizeImage_WithInvalidBytes_ReturnsInputBytesAndPngMime()
    {
        byte[] inputBytes = [1, 2, 3];

        var (resultBytes, mimeType) = _service.OptimizeImage(inputBytes, 500);

        Assert.Equal("image/png", mimeType);
        Assert.Same(inputBytes, resultBytes);
    }

    [Fact]
    public void TryProcessUploadImage_WithNonImageContentType_ReturnsFalse()
    {
        using var stream = CreateTestImageStream(100, 100);
        var result = _service.TryProcessUploadImage(stream, "application/pdf", 1000, out var processed);

        Assert.False(result);
        Assert.Null(processed.Width);
        Assert.Null(processed.ThumbnailBytes);
    }

    [Fact]
    public void TryProcessUploadImage_WithOversizedLength_ReturnsFalse()
    {
        using var stream = CreateTestImageStream(100, 100);
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 51 * 1024 * 1024, out var processed);

        Assert.False(result);
        Assert.Null(processed.Width);
    }

    [Fact]
    public void TryProcessUploadImage_WithValidImage_ProcessesAndGeneratesThumbnail()
    {
        using var stream = CreateTestImageStream(200, 100, SKEncodedImageFormat.Jpeg);
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 5000, out var processed);

        Assert.True(result);
        Assert.Equal(200, processed.Width);
        Assert.Equal(100, processed.Height);
        Assert.NotNull(processed.ThumbnailBytes);
        Assert.NotEmpty(processed.ThumbnailBytes);

        // Verify thumbnail is readable
        using var thumbBitmap = SKBitmap.Decode(processed.ThumbnailBytes);
        Assert.NotNull(thumbBitmap);
        Assert.Equal(200, thumbBitmap.Width);
        Assert.Equal(100, thumbBitmap.Height);
    }

    [Fact]
    public void TryProcessUploadImage_WithVeryLargeImage_DownscalesMainAndThumbnail()
    {
        using var stream = CreateTestImageStream(4000, 2000, SKEncodedImageFormat.Jpeg);
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 5000, out var processed);

        Assert.True(result);
        // Main scaled to max 3840
        Assert.Equal(3840, processed.Width);
        Assert.Equal(1920, processed.Height);
        Assert.NotNull(processed.ThumbnailBytes);

        // Thumbnail scaled to max 1280
        using var thumbBitmap = SKBitmap.Decode(processed.ThumbnailBytes);
        Assert.NotNull(thumbBitmap);
        Assert.Equal(1280, thumbBitmap.Width);
        Assert.Equal(640, thumbBitmap.Height);
    }

    [Fact]
    public void TryProcessUploadImage_WithCorruptedStream_ReturnsFalse()
    {
        using var stream = new MemoryStream(new byte[] { 0, 1, 2, 3, 4, 5 });
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 6, out var processed);

        Assert.False(result);
        Assert.Null(processed.Width);
        Assert.Null(processed.ThumbnailBytes);
    }

    [Fact]
    public void TryProcessUploadImage_WithCustomMaxMainDim_DownscalesToConfiguredLimit()
    {
        using var stream = CreateTestImageStream(3000, 1500, SKEncodedImageFormat.Jpeg);
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 5000, out var processed, maxMainDim: 1920, quality: 80);

        Assert.True(result);
        Assert.Equal(1920, processed.Width);
        Assert.Equal(960, processed.Height);
        Assert.NotNull(processed.ThumbnailBytes);
    }

    [Fact]
    public void TryProcessUploadImage_WithZeroMaxMainDim_PreservesOriginalDimensions()
    {
        using var stream = CreateTestImageStream(4000, 2000, SKEncodedImageFormat.Jpeg);
        var result = _service.TryProcessUploadImage(stream, "image/jpeg", 5000, out var processed, maxMainDim: 0, quality: 90);

        Assert.True(result);
        // Original dimensions preserved (no downscaling)
        Assert.Equal(4000, processed.Width);
        Assert.Equal(2000, processed.Height);
        // Thumbnail still generated for grid previews
        Assert.NotNull(processed.ThumbnailBytes);
        using var thumbBitmap = SKBitmap.Decode(processed.ThumbnailBytes);
        Assert.NotNull(thumbBitmap);
        Assert.Equal(1280, thumbBitmap.Width);
        Assert.Equal(640, thumbBitmap.Height);
    }
}
