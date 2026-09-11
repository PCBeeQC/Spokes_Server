using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Components.Pages.Chat;

public class ProgressStream : Stream
{
    private readonly Stream _innerStream;
    private readonly Action<long> _onProgressCallback;
    private readonly long _totalBytes;
    private long _bytesRead;
    private long _lastReportTime;

    public ProgressStream(Stream innerStream, long totalBytes, Action<long> onProgressCallback)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _totalBytes = totalBytes;
        _onProgressCallback = onProgressCallback ?? throw new ArgumentNullException(nameof(onProgressCallback));
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _innerStream.Read(buffer, offset, count);
        ReportProgress(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        ReportProgress(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _innerStream.ReadAsync(buffer, cancellationToken);
        ReportProgress(read);
        return read;
    }

    private void ReportProgress(int read)
    {
        if (read > 0)
        {
            _bytesRead += read;

            // Only report if we haven't reported recently, or if we finished
            long now = Environment.TickCount64;
            if (_bytesRead == _totalBytes || now - _lastReportTime > 250)
            {
                _lastReportTime = now;
                _onProgressCallback(_bytesRead);
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
