using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Security;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

/// <summary>
/// Centralized MailKit transport security and DNS-pinned SSRF protection helper for IMAP and SMTP connections.
/// Eliminates opportunistic <see cref="SecureSocketOptions.StartTlsWhenAvailable"/> downgrade risks and
/// blocks user-supplied custom mail server hosts from probing loopback, RFC1918, link-local, or metadata endpoints.
/// </summary>
public static class MailTransportSecurityHelper
{
    public const string SanitizedConnectionErrorMessage =
        "Could not establish a secure mail server connection. Please verify the hostname, port, TLS settings, and credentials.";

    public static int NormalizeImapPort(int port, bool useSsl) =>
        port > 0 ? port : (useSsl ? 993 : 143);

    public static int NormalizeSmtpPort(int port, bool useSsl) =>
        port > 0 ? port : (useSsl ? 465 : 587);

    /// <summary>
    /// Resolves strict IMAP <see cref="SecureSocketOptions"/>.
    /// Implicit TLS port 993 uses <see cref="SecureSocketOptions.SslOnConnect"/>; all other TLS ports use mandatory
    /// <see cref="SecureSocketOptions.StartTls"/> (never <see cref="SecureSocketOptions.StartTlsWhenAvailable"/>).
    /// Plaintext (<see cref="SecureSocketOptions.None"/>) is rejected unless <paramref name="allowPlaintext"/> is true.
    /// </summary>
    public static SecureSocketOptions ResolveImapTlsOptions(int port, bool useSsl, bool allowPlaintext)
    {
        var effectivePort = NormalizeImapPort(port, useSsl);
        if (!useSsl)
        {
            if (!allowPlaintext)
            {
                throw new InvalidOperationException("TLS/SSL is required for custom mail server connections.");
            }

            return SecureSocketOptions.None;
        }

        return effectivePort == 993
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    /// <summary>
    /// Resolves strict SMTP <see cref="SecureSocketOptions"/>.
    /// Implicit TLS port 465 uses <see cref="SecureSocketOptions.SslOnConnect"/>; submission/explicit ports (587, 25, 2525, etc.)
    /// use mandatory <see cref="SecureSocketOptions.StartTls"/> (never <see cref="SecureSocketOptions.StartTlsWhenAvailable"/>).
    /// Plaintext (<see cref="SecureSocketOptions.None"/>) is rejected unless <paramref name="allowPlaintext"/> is true.
    /// </summary>
    public static SecureSocketOptions ResolveSmtpTlsOptions(int port, bool useSsl, bool allowPlaintext)
    {
        var effectivePort = NormalizeSmtpPort(port, useSsl);
        if (!useSsl)
        {
            if (!allowPlaintext)
            {
                throw new InvalidOperationException("TLS/SSL is required for custom mail server connections.");
            }

            return SecureSocketOptions.None;
        }

        return effectivePort == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    /// <summary>
    /// Resolves IMAP TLS mode for <see cref="CompanyBillInboxSettings"/>.
    /// Enforces <see cref="SecureSocketOptions.SslOnConnect"/> on port 993 when <see cref="CompanyBillInboxSettings.ImapSsl"/> is true,
    /// or mandatory <see cref="SecureSocketOptions.StartTls"/> unless <see cref="CompanyBillInboxSettings.AllowPlaintextTransport"/> is explicitly enabled.
    /// </summary>
    public static SecureSocketOptions ResolveBillInboxImapTlsOptions(CompanyBillInboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectivePort = NormalizeImapPort(settings.ImapPort, settings.ImapSsl);
        if (settings.ImapSsl)
        {
            return effectivePort == 993
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
        }

        return settings.AllowPlaintextTransport
            ? SecureSocketOptions.None
            : SecureSocketOptions.StartTls;
    }

    /// <summary>
    /// Resolves SMTP TLS mode for <see cref="CompanyBillInboxSettings"/>.
    /// Historically <see cref="CompanyBillInboxSettings.SmtpPort"/> defaulted to 587 with <c>SmtpSsl = false</c> for STARTTLS;
    /// this enforces mandatory <see cref="SecureSocketOptions.StartTls"/> (or <see cref="SecureSocketOptions.SslOnConnect"/> on 465)
    /// unless <see cref="CompanyBillInboxSettings.AllowPlaintextTransport"/> is explicitly enabled.
    /// </summary>
    public static SecureSocketOptions ResolveBillInboxSmtpTlsOptions(CompanyBillInboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectivePort = NormalizeSmtpPort(settings.SmtpPort, settings.SmtpSsl);
        if (settings.SmtpSsl)
        {
            return effectivePort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
        }

        if (settings.AllowPlaintextTransport)
        {
            return SecureSocketOptions.None;
        }

        return effectivePort == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    /// <summary>
    /// Connects a MailKit <see cref="IMailService"/> (<c>ImapClient</c> or <c>SmtpClient</c>).
    /// When <paramref name="requireSafeExternalHost"/> is true, opens a DNS-pinned SSRF-validated TCP <see cref="Socket"/>
    /// via <see cref="SafeExternalHttpHandler.ConnectSafeExternalTcpSocketAsync"/> and passes the connected socket directly to MailKit.
    /// </summary>
    public static async Task ConnectMailServiceAsync(
        IMailService client,
        string host,
        int port,
        SecureSocketOptions sslOptions,
        bool requireSafeExternalHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Mail server host is required.");
        }

        var trimmedHost = host.Trim();
        if (!requireSafeExternalHost)
        {
            await client.ConnectAsync(trimmedHost, port, sslOptions, cancellationToken);
            return;
        }

        Socket socket = await SafeExternalHttpHandler.ConnectSafeExternalTcpSocketAsync(trimmedHost, port, cancellationToken);
        try
        {
            await client.ConnectAsync(socket, trimmedHost, port, sslOptions, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Formats a connection test failure message, sanitizing internal socket/DNS/banner details for non-admin callers.
    /// </summary>
    public static string FormatConnectionError(Exception ex, bool callerIsAdmin, string adminPrefix = "Connection failed: ")
    {
        ArgumentNullException.ThrowIfNull(ex);
        return callerIsAdmin
            ? $"{adminPrefix}{ex.Message}"
            : SanitizedConnectionErrorMessage;
    }
}
