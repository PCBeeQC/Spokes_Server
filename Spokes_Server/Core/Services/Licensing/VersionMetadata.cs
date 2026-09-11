using System;
using System.Reflection;

namespace Spokes_Server.Core.Services.Licensing
{
    /// <summary>
    /// Parses the application version string from the assembly's InformationalVersion attribute
    /// and exposes structured metadata for use across the application.
    /// </summary>
    public class VersionMetadata
    {
        /// <summary>
        /// The full raw version string from the assembly (e.g., "server-v2026.9.16-security").
        /// </summary>
        public string RawVersion { get; }

        /// <summary>
        /// The clean numeric version for comparison (e.g., "2026.9.16").
        /// Strips prefix (server-v, test-v, beta-v) and suffix (-security, +metadata).
        /// </summary>
        public string CleanVersion { get; }

        /// <summary>
        /// The release channel: "server", "beta", "test", or "unknown" (dev builds).
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// True if this build is tagged as a security patch release.
        /// Detected by the presence of "-security" suffix in the version string.
        /// </summary>
        public bool IsSecurityPatch { get; }

        public VersionMetadata()
        {
            RawVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

            // Detect security patch from suffix
            IsSecurityPatch = RawVersion.Contains("-security", StringComparison.OrdinalIgnoreCase);

            // Determine channel from prefix
            if (RawVersion.StartsWith("test-v"))
                Channel = "test";
            else if (RawVersion.StartsWith("beta-v"))
                Channel = "beta";
            else if (RawVersion.StartsWith("server-v"))
                Channel = "server";
            else
                Channel = "unknown";

            // Clean version: strip prefix (find first digit) and suffix (after - or +)
            CleanVersion = GetCleanVersion(RawVersion);
        }

        // Internal constructor for unit testing with explicit version string
        internal VersionMetadata(string rawVersion)
        {
            RawVersion = rawVersion;
            IsSecurityPatch = RawVersion.Contains("-security", StringComparison.OrdinalIgnoreCase);

            if (RawVersion.StartsWith("test-v"))
                Channel = "test";
            else if (RawVersion.StartsWith("beta-v"))
                Channel = "beta";
            else if (RawVersion.StartsWith("server-v"))
                Channel = "server";
            else
                Channel = "unknown";

            CleanVersion = GetCleanVersion(RawVersion);
        }

        private static string GetCleanVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "0.0.0";

            int firstDigitIndex = -1;
            for (int i = 0; i < version.Length; i++)
            {
                if (char.IsDigit(version[i]))
                {
                    firstDigitIndex = i;
                    break;
                }
            }

            string cleanVersion = firstDigitIndex >= 0 ? version.Substring(firstDigitIndex) : version.TrimStart('v', 'V');

            int plusIndex = cleanVersion.IndexOf('+');
            if (plusIndex >= 0) cleanVersion = cleanVersion.Substring(0, plusIndex);

            int minusIndex = cleanVersion.IndexOf('-');
            if (minusIndex >= 0) cleanVersion = cleanVersion.Substring(0, minusIndex);

            return cleanVersion;
        }
    }
}
