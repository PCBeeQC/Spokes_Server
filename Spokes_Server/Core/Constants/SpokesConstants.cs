namespace Spokes_Server.Core.Constants;

public static class SpokesConstants
{
    public const string PushRelayUrl = "https://push.spokes.sh/api/relay-push";
    public const string GifRelayUrl = "https://push.spokes.sh/api/relay-gif";
    public const string TelemetryRelayUrl = "https://push.spokes.sh/api/relay-telemetry";

    /// <summary>
    /// Legacy fallback for PushRelayKey. In official Docker builds, injected via environment variable SPOKES_RELAY_KEY.
    /// Prefer injecting IOptions&lt;SpokesOptions&gt;.
    /// </summary>
    [Obsolete("Prefer injecting IOptions<SpokesOptions> instead of reading static constants.")]
    public static string PushRelayKey =>
        Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY") ?? string.Empty;

    /// <summary>
    /// Legacy fallback for DefaultKlipyApiKey. In official Docker builds, injected via environment variable SPOKES_KLIPY_KEY.
    /// Prefer injecting IOptions&lt;SpokesOptions&gt;.
    /// </summary>
    [Obsolete("Prefer injecting IOptions<SpokesOptions> instead of reading static constants.")]
    public static string DefaultKlipyApiKey =>
        Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY") ?? string.Empty;

    public const string LicensePublicKeyPem = @"-----BEGIN RSA PUBLIC KEY-----
MIIBCgKCAQEAvVU8XQ1jra44+p7gvCmKPH9WgNBGLU0uwlFGJXMrlVmlHg770NsH
Rb9kR4tY5jyQnAaCCjAZKojD3FKaGmT2xpaAmdFvLHOX2PWhUJ7aX/Xi9Qcuapj3
ip+aYqvDtDWkgF9lU0LEtUgBoMQFEZveppcUJKT8rGtGRaSr4upl+XueM7nudM4I
p21ALVNHc02lvgcKrCsexGNFbGLa9LgSUdb0o8k/wUUuTXx8ZkMfS+XbrBiT248D
nSqFRyskexvaNa/xqdCtjM4Gmo0NhhbV/X5jSLU3a/+LoR76ZhmJVpBKTaK72wi2
wnMkoyiZs/d5xb8Ia+1MgObX6nlvvufBTQIDAQAB
-----END RSA PUBLIC KEY-----";
}
