namespace Spokes_Server.Core.Constants
{
    public static class SpokesConstants
    {
        public const string PushRelayUrl = "https://push.spokes.sh/api/relay-push";

        /// <summary>
        /// Push relay authentication key. Injected via environment variable in official Docker builds.
        /// Self-compiled instances will not have push notification relay access without a valid key.
        /// </summary>
        public static string PushRelayKey =>
            Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY")
            ?? "";

        /// <summary>
        /// Klipy GIF search API key. Injected via environment variable in official Docker builds.
        /// Self-compiled instances should provide their own key or GIF search will be unavailable.
        /// </summary>
        public static string DefaultKlipyApiKey =>
            Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY")
            ?? "";

        /// <summary>
        /// HMAC salt used to sign demo creation version timestamps.
        /// Injected via environment variable in official Docker builds to prevent demo clock tampering.
        /// </summary>
        public static string LicenseHMACSalt =>
            Environment.GetEnvironmentVariable("SPOKES_HMAC_SALT")
            ?? "default-development-salt";

        public const string LicensePublicKeyPem = @"-----BEGIN RSA PUBLIC KEY-----
MIIBCgKCAQEAvVU8XQ1jra44+p7gvCmKPH9WgNBGLU0uwlFGJXMrlVmlHg770NsH
Rb9kR4tY5jyQnAaCCjAZKojD3FKaGmT2xpaAmdFvLHOX2PWhUJ7aX/Xi9Qcuapj3
ip+aYqvDtDWkgF9lU0LEtUgBoMQFEZveppcUJKT8rGtGRaSr4upl+XueM7nudM4I
p21ALVNHc02lvgcKrCsexGNFbGLa9LgSUdb0o8k/wUUuTXx8ZkMfS+XbrBiT248D
nSqFRyskexvaNa/xqdCtjM4Gmo0NhhbV/X5jSLU3a/+LoR76ZhmJVpBKTaK72wi2
wnMkoyiZs/d5xb8Ia+1MgObX6nlvvufBTQIDAQAB
-----END RSA PUBLIC KEY-----";
    }
}
