using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

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
    public void ReportProgress_WhenIntervalExceeds250Ms_InvokesCallback()
    {
        var timeProvider = new TestTimeProvider();
        var data = new byte[100];
        using var inner = new MemoryStream(data);
        var reports = new List<long>();
        using var stream = new ProgressStream(inner, 100, p => reports.Add(p), timeProvider);

        var buffer = new byte[10];
        int r1 = stream.Read(buffer, 0, 10);
        Assert.Equal(10, r1);

        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

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
