using SkiaSharp;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

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

        var resultBytes = _service.OptimizeImage(inputBytes, 500, out var mimeType);

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

        var resultBytes = _service.OptimizeImage(inputBytes, 500, out var mimeType);

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

        var resultBytes = _service.OptimizeImage(inputBytes, 500, out var mimeType);

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

        var resultBytes = _service.OptimizeImage(inputBytes, 400, out var mimeType);

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

        var resultBytes = _service.OptimizeImage(inputBytes, 500, out var mimeType);

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

        var resultBytes = _service.OptimizeImage(inputBytes, 500, out var mimeType);

        Assert.Equal("image/png", mimeType);
        Assert.Same(inputBytes, resultBytes);
    }
}
