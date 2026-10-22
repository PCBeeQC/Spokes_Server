using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
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
    Task<ImapConnectionResult> ConnectAsync(string employeeId, CancellationToken cancellationToken = default);
    Task<ImapConnectionResult> ConnectAsync(Employee employee, CancellationToken cancellationToken = default);
    Task<ImapClient?> CreateAuthenticatedClientAsync(string employeeId, CancellationToken cancellationToken = default);
    Task<ImapClient?> CreateAuthenticatedClientAsync(Employee employee, CancellationToken cancellationToken = default);
}
