using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Spokes_Server.Components.Pages.Chat;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages.Chat;

public class ProgressStreamTests
{
    private class TrackableMemoryStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        public TrackableMemoryStream() { }
        public TrackableMemoryStream(byte[] buffer) : base(buffer) { }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Constructor_NullInnerStream_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>("innerStream", () => new ProgressStream(null!, 100, _ => { }));
    }

    [Fact]
    public void Constructor_NullOnProgressCallback_ThrowsArgumentNullException()
    {
        using var inner = new MemoryStream();
        Assert.Throws<ArgumentNullException>("onProgressCallback", () => new ProgressStream(inner, 100, null!));
    }

    [Fact]
    public void Constructor_ValidParameters_InitializesStream()
    {
        using var inner = new MemoryStream();
        using var stream = new ProgressStream(inner, 100, _ => { });

        Assert.NotNull(stream);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanRead_DelegatesToInnerStream_ReturnsInnerStreamValue(bool canRead)
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.CanRead).Returns(canRead);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });

        Assert.Equal(canRead, stream.CanRead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanSeek_DelegatesToInnerStream_ReturnsInnerStreamValue(bool canSeek)
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.CanSeek).Returns(canSeek);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });

        Assert.Equal(canSeek, stream.CanSeek);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanWrite_DelegatesToInnerStream_ReturnsInnerStreamValue(bool canWrite)
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.CanWrite).Returns(canWrite);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });

        Assert.Equal(canWrite, stream.CanWrite);
    }

    [Fact]
    public void Length_DelegatesToInnerStream_ReturnsInnerStreamValue()
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.Length).Returns(42L);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });

        Assert.Equal(42L, stream.Length);
    }

    [Fact]
    public void Position_Get_DelegatesToInnerStream()
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.Position).Returns(15L);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });

        Assert.Equal(15L, stream.Position);
    }

    [Fact]
    public void Position_Set_DelegatesToInnerStream()
    {
        var mockInner = new Mock<Stream>();
        mockInner.SetupProperty(s => s.Position, 0L);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });
        stream.Position = 25L;

        Assert.Equal(25L, mockInner.Object.Position);
    }

    [Fact]
    public void Seek_DelegatesToInnerStream_ReturnsNewPosition()
    {
        var mockInner = new Mock<Stream>();
        mockInner.Setup(s => s.Seek(10L, SeekOrigin.Current)).Returns(20L);

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });
        var result = stream.Seek(10L, SeekOrigin.Current);

        Assert.Equal(20L, result);
        mockInner.Verify(s => s.Seek(10L, SeekOrigin.Current), Times.Once);
    }

    [Fact]
    public void SetLength_DelegatesToInnerStream_CallsInnerStream()
    {
        var mockInner = new Mock<Stream>();

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });
        stream.SetLength(50L);

        mockInner.Verify(s => s.SetLength(50L), Times.Once);
    }

    [Fact]
    public void Write_DelegatesToInnerStream_CallsInnerStream()
    {
        var mockInner = new Mock<Stream>();
        byte[] buffer = [1, 2, 3];

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });
        stream.Write(buffer, 0, 3);

        mockInner.Verify(s => s.Write(buffer, 0, 3), Times.Once);
    }

    [Fact]
    public void Flush_DelegatesToInnerStream_CallsInnerStream()
    {
        var mockInner = new Mock<Stream>();

        using var stream = new ProgressStream(mockInner.Object, 100, _ => { });
        stream.Flush();

        mockInner.Verify(s => s.Flush(), Times.Once);
    }

    [Fact]
    public void Dispose_WhenCalled_DisposesInnerStream()
    {
        var trackable = new TrackableMemoryStream();
        var stream = new ProgressStream(trackable, 100, _ => { });

        stream.Dispose();

        Assert.True(trackable.IsDisposed);
    }

    [Fact]
    public void Read_SynchronousRead_ReadsBytesAndInvokesCallback()
    {
        byte[] data = [1, 2, 3, 4, 5];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 5, p => reports.Add(p));

        var buffer = new byte[5];
        int bytesRead = stream.Read(buffer, 0, 5);

        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer);
        Assert.Single(reports);
        Assert.Equal(5L, reports[0]);
    }

    [Fact]
    public void Read_WhenZeroBytesRead_DoesNotInvokeCallback()
    {
        using var inner = new MemoryStream();
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 10, p => reports.Add(p));

        var buffer = new byte[10];
        int bytesRead = stream.Read(buffer, 0, 10);

        Assert.Equal(0, bytesRead);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ReadAsync_ByteArray_ReadsBytesAndInvokesCallback()
    {
        byte[] data = [10, 20, 30];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 3, p => reports.Add(p));

        var buffer = new byte[3];
        int bytesRead = await stream.ReadAsync(buffer, 0, 3, CancellationToken.None);

        Assert.Equal(3, bytesRead);
        Assert.Equal(data, buffer);
        Assert.Single(reports);
        Assert.Equal(3L, reports[0]);
    }

    [Fact]
    public async Task ReadAsync_MemoryBuffer_ReadsBytesAndInvokesCallback()
    {
        byte[] data = [7, 8, 9, 10];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 4, p => reports.Add(p));

        var buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);

        Assert.Equal(4, bytesRead);
        Assert.Equal(data, buffer);
        Assert.Single(reports);
        Assert.Equal(4L, reports[0]);
    }

    [Fact]
    public void ReportProgress_RapidReadsUnderThrottle_OnlyInvokesOnFirstAndCompletion()
    {
        var data = new byte[40];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 40, p => reports.Add(p));

        var buffer = new byte[10];
        int r1 = stream.Read(buffer, 0, 10);
        int r2 = stream.Read(buffer, 0, 10);
        int r3 = stream.Read(buffer, 0, 10);
        int r4 = stream.Read(buffer, 0, 10);

        Assert.Equal(10, r1);
        Assert.Equal(10, r2);
        Assert.Equal(10, r3);
        Assert.Equal(10, r4);

        Assert.Equal(2, reports.Count);
        Assert.Equal(10L, reports[0]);
        Assert.Equal(40L, reports[1]);
    }

    [Fact]
    public async Task ReportProgress_WhenIntervalExceeds250Ms_InvokesCallback()
    {
        var data = new byte[100];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 100, p => reports.Add(p));

        var buffer = new byte[10];
        int r1 = stream.Read(buffer, 0, 10);
        Assert.Equal(10, r1);
        await Task.Delay(300);
        int r2 = stream.Read(buffer, 0, 10);
        Assert.Equal(10, r2);

        Assert.Equal(2, reports.Count);
        Assert.Equal(10L, reports[0]);
        Assert.Equal(20L, reports[1]);
    }

    [Fact]
    public async Task ReadAsync_CumulativeBytesAcrossBursts_ReportsAccurateByteCount()
    {
        var data = new byte[20];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 20, p => reports.Add(p));

        var buffer = new byte[5];
        for (int i = 0; i < 4; i++)
        {
            int read = await stream.ReadAsync(buffer, 0, 5);
            Assert.Equal(5, read);
        }

        Assert.Contains(20L, reports);
        Assert.Equal(20L, reports[^1]);
    }
}
