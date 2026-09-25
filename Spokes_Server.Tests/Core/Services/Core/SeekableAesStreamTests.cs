using System.Security.Cryptography;
using System.Text;
using Moq;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class SeekableAesStreamTests
{
    private MemoryStream CreateEncryptedStream(byte[] plainText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var ms = new MemoryStream();
        ms.Write(aes.IV, 0, 16);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            cs.Write(plainText, 0, plainText.Length);
            cs.FlushFinalBlock();
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Constructor_NullBaseStream_ThrowsArgumentNullException()
    {
        byte[] key = new byte[32];
        Assert.Throws<ArgumentNullException>(() => new SeekableAesStream(null!, key));
    }

    [Fact]
    public void Constructor_NullKey_ThrowsArgumentNullException()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => new SeekableAesStream(ms, null!));
    }

    [Fact]
    public void Constructor_NonSeekableStream_ThrowsArgumentException()
    {
        var mockStream = new Mock<Stream>();
        mockStream.Setup(s => s.CanSeek).Returns(false);
        mockStream.Setup(s => s.CanRead).Returns(true);
        byte[] key = new byte[32];

        var ex = Assert.Throws<ArgumentException>(() => new SeekableAesStream(mockStream.Object, key));
        Assert.Contains("Base stream must be readable and seekable", ex.Message);
    }

    [Fact]
    public void Constructor_NonReadableStream_ThrowsArgumentException()
    {
        var mockStream = new Mock<Stream>();
        mockStream.Setup(s => s.CanSeek).Returns(true);
        mockStream.Setup(s => s.CanRead).Returns(false);
        byte[] key = new byte[32];

        var ex = Assert.Throws<ArgumentException>(() => new SeekableAesStream(mockStream.Object, key));
        Assert.Contains("Base stream must be readable and seekable", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(35)]
    [InlineData(47)]
    public void Constructor_InvalidFileLength_SetsLengthToZero(int length)
    {
        byte[] key = new byte[32];
        using var ms = new MemoryStream(new byte[length]);
        using var stream = new SeekableAesStream(ms, key);

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Constructor_MalformedPadding_SetsLengthWithZeroPaddingLen()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var rawBlock = new byte[16];
        rawBlock[15] = 0; // Malformed PKCS7 padding byte (0 is invalid padding)

        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, 16);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            cs.Write(rawBlock, 0, rawBlock.Length);
            cs.FlushFinalBlock();
        }
        ms.Position = 0;

        using var stream = new SeekableAesStream(ms, key);
        // fileLen = 32; fileLen - 16 - paddingLen(0) = 16
        Assert.Equal(16, stream.Length);
    }

    [Fact]
    public void Read_SequentialFullRead_DecryptsMatchingOriginalPlainText()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog and runs across the field to find shelter!");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        Assert.Equal(plainText.Length, seekableStream.Length);
        byte[] buffer = new byte[plainText.Length];
        seekableStream.ReadExactly(buffer, 0, buffer.Length);

        Assert.Equal(plainText, buffer);
    }

    [Fact]
    public void Read_AcrossBlockBoundary_CanReturnPartialBytesRequiringMultipleReads()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!@#$%^&*()_+");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        // Seek to byte 59 (offset 11 in block 3, leaving 5 bytes in the current 16-byte block)
        seekableStream.Seek(59, SeekOrigin.Begin);
        byte[] buffer = new byte[15];

        // The first read consumes the remaining 5 bytes in the current cipher block
        int firstRead = seekableStream.Read(buffer, 0, 15);
        Assert.Equal(5, firstRead);
        Assert.Equal(64, seekableStream.Position);

        // The second read consumes the remaining 10 bytes from the subsequent block
        int secondRead = seekableStream.Read(buffer, firstRead, 15 - firstRead);
        Assert.Equal(10, secondRead);
        Assert.Equal(74, seekableStream.Position);
        Assert.Equal(plainText[59..74], buffer);
    }

    [Fact]
    public void Seek_BeginCurrentEnd_ReadsCorrectOffsetBytes()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!@#$%^&*()_+");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        // SeekOrigin.Begin
        long pos = seekableStream.Seek(20, SeekOrigin.Begin);
        Assert.Equal(20, pos);
        Assert.Equal(20, seekableStream.Position);

        byte[] buf1 = new byte[10];
        seekableStream.ReadExactly(buf1, 0, 10);
        Assert.Equal(plainText[20..30], buf1);
        Assert.Equal(30, seekableStream.Position);

        // SeekOrigin.Current
        pos = seekableStream.Seek(5, SeekOrigin.Current);
        Assert.Equal(35, pos);
        Assert.Equal(35, seekableStream.Position);

        byte[] buf2 = new byte[10];
        seekableStream.ReadExactly(buf2, 0, 10);
        Assert.Equal(plainText[35..45], buf2);
        Assert.Equal(45, seekableStream.Position);

        // SeekOrigin.End
        pos = seekableStream.Seek(-15, SeekOrigin.End);
        Assert.Equal(plainText.Length - 15, pos);
        Assert.Equal(plainText.Length - 15, seekableStream.Position);

        byte[] buf3 = new byte[15];
        seekableStream.ReadExactly(buf3, 0, 15);
        Assert.Equal(plainText[^15..], buf3);
        Assert.Equal(plainText.Length, seekableStream.Position);

        // Position setter
        seekableStream.Position = 5;
        Assert.Equal(5, seekableStream.Position);

        byte[] buf4 = new byte[5];
        seekableStream.ReadExactly(buf4, 0, 5);
        Assert.Equal(plainText[5..10], buf4);
    }

    [Fact]
    public void Seek_BeyondLength_ClampsToLengthAndReturnsZeroOnRead()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("Test string with some data.");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        long pos = seekableStream.Seek(1000, SeekOrigin.Begin);
        Assert.Equal(seekableStream.Length, pos);
        Assert.Equal(seekableStream.Length, seekableStream.Position);

        byte[] buffer = new byte[10];
        int read = seekableStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(0, read);
    }

    [Fact]
    public void Seek_NegativeOffset_ClampsToZero()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("Testing negative seek offsets.");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        long pos = seekableStream.Seek(-50, SeekOrigin.Begin);
        Assert.Equal(0, pos);
        Assert.Equal(0, seekableStream.Position);

        // From Current
        seekableStream.Seek(10, SeekOrigin.Begin);
        pos = seekableStream.Seek(-30, SeekOrigin.Current);
        Assert.Equal(0, pos);
        Assert.Equal(0, seekableStream.Position);

        // From End
        pos = seekableStream.Seek(-1000, SeekOrigin.End);
        Assert.Equal(0, pos);
        Assert.Equal(0, seekableStream.Position);
    }

    [Fact]
    public void Seek_SamePosition_ReturnsCurrentPosition()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("Testing seeking to the current position.");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        byte[] buf = new byte[5];
        int bytesRead = seekableStream.Read(buf, 0, 5);
        Assert.Equal(5, bytesRead);
        long originalPos = seekableStream.Position;

        long newPos = seekableStream.Seek(0, SeekOrigin.Current);
        Assert.Equal(originalPos, newPos);
    }

    [Fact]
    public void Seek_InvalidOrigin_ThrowsArgumentException()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = Encoding.UTF8.GetBytes("Testing invalid seek origin.");
        using var encStream = CreateEncryptedStream(plainText, key);
        using var seekableStream = new SeekableAesStream(encStream, key);

        Assert.Throws<ArgumentException>(() => seekableStream.Seek(0, (SeekOrigin)999));
    }

    [Fact]
    public void Properties_CanReadCanSeekCanWrite_AreCorrect()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = new byte[32];
        using var encStream = CreateEncryptedStream(plainText, key);
        using var stream = new SeekableAesStream(encStream, key);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void WriteAndSetLength_ThrowNotSupportedException()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = new byte[32];
        using var encStream = CreateEncryptedStream(plainText, key);
        using var stream = new SeekableAesStream(encStream, key);

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[5], 0, 5));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(10));

        // Flush is a no-op and should not throw
        stream.Flush();
    }

    [Fact]
    public void Read_AfterDisposed_ThrowsObjectDisposedException()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = new byte[32];
        var encStream = CreateEncryptedStream(plainText, key);
        var stream = new SeekableAesStream(encStream, key);
        stream.Dispose();

        byte[] buffer = new byte[10];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Seek_AfterDisposed_ThrowsObjectDisposedException()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = new byte[32];
        var encStream = CreateEncryptedStream(plainText, key);
        var stream = new SeekableAesStream(encStream, key);
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void Dispose_DisposesBaseStreamAndCryptoResources()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plainText = new byte[32];
        var encStream = CreateEncryptedStream(plainText, key);
        var stream = new SeekableAesStream(encStream, key);

        var buf = new byte[5];
        int bytesRead = stream.Read(buf, 0, 5);
        Assert.Equal(5, bytesRead);

        stream.Dispose();

        // Base stream should be disposed; accessing its Position or Read should throw ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(() => encStream.Read(buf, 0, 1));

        // Subsequent call to Dispose should be idempotent
        stream.Dispose();
    }
}
