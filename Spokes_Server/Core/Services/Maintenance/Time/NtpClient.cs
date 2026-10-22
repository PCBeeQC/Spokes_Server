using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Maintenance.Time;

public record NtpSyncResult(
    bool Success,
    TimeSpan Offset,
    DateTime NtpTimeUtc,
    double RoundTripDelayMs,
    string? ErrorMessage = null
);

/// <summary>
/// SNTP (RFC 4330) client for querying network time servers over UDP port 123.
/// </summary>
public class NtpClient
{
    private const int DefaultNtpPort = 123;
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Queries the specified NTP server and returns the measured clock offset.
    /// </summary>
    public async Task<NtpSyncResult> QueryAsync(string host, int timeoutMs = 4000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, "NTP host address cannot be empty.");
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses == null || addresses.Length == 0)
            {
                return new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, $"Unable to resolve host: {host}");
            }

            var targetAddress = addresses[0];
            var endPoint = new IPEndPoint(targetAddress, DefaultNtpPort);

            using var udpClient = new UdpClient();
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var ntpPacket = new byte[48];
            // LI = 0 (no warning), VN = 3 (IPv4/IPv6 SNTP), Mode = 3 (Client) -> 0x1B
            ntpPacket[0] = 0x1B;

            var t1Utc = DateTime.UtcNow; // Originate timestamp
            await udpClient.SendAsync(ntpPacket, ntpPacket.Length, endPoint);

            var receiveResult = await udpClient.ReceiveAsync(linkedCts.Token);
            var t4Utc = DateTime.UtcNow; // Destination timestamp

            var buffer = receiveResult.Buffer;
            if (buffer == null || buffer.Length < 48)
            {
                return new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, "Invalid NTP response packet length.");
            }

            // Transmit timestamp (T3) starts at byte 40
            var t3Utc = ParseTimestamp(buffer, 40);

            // Receive timestamp (T2) starts at byte 32
            var t2Utc = ParseTimestamp(buffer, 32);

            TimeSpan roundTripDelay;
            TimeSpan clockOffset;

            if (t2Utc > NtpEpoch)
            {
                // Full RFC 4330 4-timestamp formula
                roundTripDelay = (t4Utc - t1Utc) - (t3Utc - t2Utc);
                clockOffset = ((t2Utc - t1Utc) + (t3Utc - t4Utc)) / 2;
            }
            else
            {
                // Basic formula when T2 is omitted
                roundTripDelay = t4Utc - t1Utc;
                clockOffset = t3Utc - t4Utc + TimeSpan.FromTicks(roundTripDelay.Ticks / 2);
            }

            var ntpTimeUtc = DateTime.UtcNow + clockOffset;
            return new NtpSyncResult(true, clockOffset, ntpTimeUtc, Math.Max(0, roundTripDelay.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            return new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, $"NTP query to {host} timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            return new NtpSyncResult(false, TimeSpan.Zero, DateTime.UtcNow, 0, ex.Message);
        }
    }

    private static DateTime ParseTimestamp(byte[] buffer, int offset)
    {
        ulong intPart = ((ulong)buffer[offset] << 24) |
                        ((ulong)buffer[offset + 1] << 16) |
                        ((ulong)buffer[offset + 2] << 8) |
                        buffer[offset + 3];

        ulong fractPart = ((ulong)buffer[offset + 4] << 24) |
                          ((ulong)buffer[offset + 5] << 16) |
                          ((ulong)buffer[offset + 6] << 8) |
                          buffer[offset + 7];

        if (intPart == 0 && fractPart == 0)
        {
            return DateTime.MinValue;
        }

        double milliseconds = (intPart * 1000.0) + ((fractPart * 1000.0) / 0x100000000L);
        return NtpEpoch.AddMilliseconds(milliseconds);
    }
}
