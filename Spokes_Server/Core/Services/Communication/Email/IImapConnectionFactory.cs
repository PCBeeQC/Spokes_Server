using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Email;

public enum ImapConnectionStatus
{
    Success,
    ServerNotConfigured,
    MissingCredentials,
    DecryptionError,
    ConnectionFailed
}

public sealed record ImapConnectionResult(
    ImapClient? Client,
    ImapConnectionStatus Status,
    string? ErrorMessage = null) : IDisposable
{
    public bool IsSuccess => Status == ImapConnectionStatus.Success && Client != null;

    public void Dispose() => Client?.Dispose();
}

public interface IImapConnectionFactory
{
    Task<ImapConnectionResult> ConnectAsync(string accountId, CancellationToken cancellationToken = default);
    Task<ImapConnectionResult> ConnectAsync(EmailAccount account, CancellationToken cancellationToken = default);
    Task<ImapClient?> CreateAuthenticatedClientAsync(string accountId, CancellationToken cancellationToken = default);
    Task<ImapClient?> CreateAuthenticatedClientAsync(EmailAccount account, CancellationToken cancellationToken = default);
    Task<ImapConnectionResult> TestConnectionAsync(string host, int port, bool useSsl, string username, string password, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> TestConnectionAsync(EmailAccount account, string? plainPasswordOverride = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> TestConnectionAsync(EmailAccount account, string? plainPasswordOverride, Employee? caller, CancellationToken cancellationToken = default);
    Task<string?> TryDetectSenderDisplayNameAsync(EmailAccount account, string? plainPasswordOverride = null, CancellationToken cancellationToken = default);
}
