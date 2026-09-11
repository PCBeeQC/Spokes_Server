using System;
using System.IO;
using System.Security.Cryptography;

namespace Spokes_Server.Core.Services.Core;

public class SeekableAesStream : Stream
{
    private readonly Stream _baseStream;
    private readonly byte[] _key;
    private readonly long _length;
    private long _position;
    private CryptoStream? _cryptoStream;
    private ICryptoTransform? _decryptor;
    private bool _disposed;

    public SeekableAesStream(Stream baseStream, byte[] key)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _key = key ?? throw new ArgumentNullException(nameof(key));

        if (!_baseStream.CanSeek || !_baseStream.CanRead)
            throw new ArgumentException("Base stream must be readable and seekable.");

        // Calculate plaintext length
        // File structure: 16 bytes IV + N blocks of ciphertext (last block is PKCS7 padded)
        long fileLen = _baseStream.Length;

        if (fileLen < 32 || fileLen % 16 != 0)
        {
            // Invalid AES CBC file structure.
            _length = 0;
            return;
        }

        // Read the last 32 bytes (or 32 bytes if the file is exactly 32 bytes)
        _baseStream.Seek(-32, SeekOrigin.End);
        var lastTwoBlocks = new byte[32];

        int bytesRead = 0;
        while (bytesRead < 32)
        {
            int r = _baseStream.Read(lastTwoBlocks, bytesRead, 32 - bytesRead);
            if (r == 0) break;
            bytesRead += r;
        }

        if (bytesRead < 32)
        {
            _length = 0;
            return;
        }

        var ivForLastBlock = new byte[16];
        Buffer.BlockCopy(lastTwoBlocks, 0, ivForLastBlock, 0, 16);

        var lastCipherBlock = new byte[16];
        Buffer.BlockCopy(lastTwoBlocks, 16, lastCipherBlock, 0, 16);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = ivForLastBlock;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None; // We decrypt raw to examine the padding byte

        using var decryptor = aes.CreateDecryptor();
        var plainBlock = decryptor.TransformFinalBlock(lastCipherBlock, 0, 16);

        int paddingLen = plainBlock[15];
        if (paddingLen < 1 || paddingLen > 16)
        {
            paddingLen = 0; // Malformed padding
        }

        _length = fileLen - 16 - paddingLen;

        // Initialize at the beginning
        Seek(0, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SeekableAesStream));

        if (_position >= _length) return 0;

        if (_cryptoStream == null)
            ResetCryptoStream();

        // Prevent reading past the calculated padding (CryptoStream with PKCS7 does this automatically,
        // but since we might be jumping around, it's safer to clamp it manually and use PaddingMode.None)
        long remaining = _length - _position;
        int toRead = count < remaining ? count : (int)remaining;

        int bytesRead = _cryptoStream!.Read(buffer, offset, toRead);
        _position += bytesRead;
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SeekableAesStream));

        long targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin")
        };

        if (targetPosition < 0) targetPosition = 0;
        if (targetPosition > _length) targetPosition = _length;

        if (targetPosition == _position && _cryptoStream != null)
            return _position;

        _position = targetPosition;
        ResetCryptoStream();
        return _position;
    }

    private void ResetCryptoStream()
    {
        _cryptoStream?.Dispose();
        _decryptor?.Dispose();

        long blockIndex = _position / 16;
        int blockOffset = (int)(_position % 16);

        // If blockIndex == 0, the IV is at file offset 0.
        // If blockIndex > 0, the IV is the preceding ciphertext block at file offset 16 + (blockIndex - 1) * 16.
        long ivPosition = blockIndex == 0 ? 0 : 16 + (blockIndex - 1) * 16;
        _baseStream.Seek(ivPosition, SeekOrigin.Begin);

        var iv = new byte[16];
        int read = 0;
        while (read < 16)
        {
            int r = _baseStream.Read(iv, read, 16 - read);
            if (r == 0) break;
            read += r;
        }

        var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        // Using None allows us to stop reading precisely at the clamped `_length` without CryptoStream
        // throwing exceptions because the stream ended before valid PKCS7 padding was detected.
        aes.Padding = PaddingMode.None;

        _decryptor = aes.CreateDecryptor();

        // Leave open so we can continue seeking the base stream manually
        _cryptoStream = new CryptoStream(_baseStream, _decryptor, CryptoStreamMode.Read, leaveOpen: true);

        // Discard the offset bytes within the current block
        if (blockOffset > 0)
        {
            var discard = new byte[blockOffset];
            int discarded = 0;
            while (discarded < blockOffset)
            {
                int r = _cryptoStream.Read(discard, discarded, blockOffset - discarded);
                if (r == 0) break;
                discarded += r;
            }
        }
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cryptoStream?.Dispose();
                _decryptor?.Dispose();
                _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
