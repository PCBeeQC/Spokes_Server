using System;
using System.Threading.Tasks;
using Xunit;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Tests.Core.Security;

public class SafeExternalHttpHandlerTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    public async Task ConnectSafeExternalTcpSocketAsync_RejectsNonPublicLiteralIpAddresses(string ipHost)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeExternalHttpHandler.ConnectSafeExternalTcpSocketAsync(ipHost, 993));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST.")]
    [InlineData("mail.localhost")]
    [InlineData("printer.local")]
    [InlineData("smtp.internal")]
    [InlineData("metadata.google.internal")]
    [InlineData("host.docker.internal")]
    [InlineData("router.home.arpa")]
    public async Task ConnectSafeExternalTcpSocketAsync_RejectsBlockedHostnames(string host)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeExternalHttpHandler.ConnectSafeExternalTcpSocketAsync(host, 587));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task ConnectSafeExternalTcpSocketAsync_RejectsInvalidPorts(int invalidPort)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeExternalHttpHandler.ConnectSafeExternalTcpSocketAsync("8.8.8.8", invalidPort));
    }
}
