using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Services.Communication
{
    /// <summary>
    /// Sends an encrypted telemetry ping to console.spokes.sh every 5 days.
    /// This is OPT-OUT via Admin Settings → "Send Anonymous Usage Statistics".
    ///
    /// Data sent (AES-GCM encrypted, only decryptable by PCBee QC):
    ///   - ServerId (anonymous instance GUID)
    ///   - LicenseId (license key ID or "Demo")
    ///   - OS description
    ///   - Active user count
    ///   - Server edition and version
    ///
    /// No personal data, chat messages, file contents, or IP addresses are included in the payload.
    /// </summary>
    public class TelemetryBackgroundService : BackgroundService
    {
        private readonly Database _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TelemetryBackgroundService> _logger;

        public TelemetryBackgroundService(Database db, IHttpClientFactory httpClientFactory, ILogger<TelemetryBackgroundService> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait 10 seconds after startup before first ping
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var profile = _db.CompanyProfile.Get();
                    var serverConfig = _db.ServerConfigs.GetOrCreateGlobalConfig();

                    if (profile != null && profile.SendUsageStatistics)
                    {
                        string licenseId = "Demo";
                        if (!string.IsNullOrEmpty(profile.LicensePayload))
                        {
                            try
                            {
                                var options = new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true,
                                    AllowTrailingCommas = true
                                };
                                var licenseInfo = JsonSerializer.Deserialize<SpokesLicenseFile>(profile.LicensePayload, options);
                                if (licenseInfo != null && !string.IsNullOrEmpty(licenseInfo.LicenseId))
                                {
                                    licenseId = licenseInfo.LicenseId;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Ignore parsing errors, default to Demo
                                _logger.LogWarning(ex, "Failed to parse license payload in telemetry service, falling back to Demo.");
                            }
                        }

                        var stats = new Spokes_Server.Core.Models.Core.UsageStatistics
                        {
                            ServerId = serverConfig.DatabaseCreationId,
                            LicenseId = licenseId,
                            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                            ActiveUsers = _db.Employees.GetAll().Count(e => e.IsActive),
                            ServerEdition = profile.Edition ?? "Unknown",
                            ServerVersion = LicenseValidationService.AppVersion
                        };

                        var payloadWrapper = new
                        {
                            Stats = stats,
                            TimestampUtc = DateTime.UtcNow
                        };

                        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadWrapper));

                        var aesKey = new byte[32];
                        RandomNumberGenerator.Fill(aesKey);
                        using var aesGcm = new AesGcm(aesKey, 16);

                        var aesNonce = new byte[12];
                        RandomNumberGenerator.Fill(aesNonce);

                        var ciphertext = new byte[jsonBytes.Length];
                        var authTag = new byte[16];

                        aesGcm.Encrypt(aesNonce, jsonBytes, ciphertext, authTag);

                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(SpokesConstants.LicensePublicKeyPem);
                        var encryptedAesKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

                        var postPayload = new
                        {
                            EncryptedKey = Convert.ToBase64String(encryptedAesKey),
                            Nonce = Convert.ToBase64String(aesNonce),
                            Ciphertext = Convert.ToBase64String(ciphertext),
                            Tag = Convert.ToBase64String(authTag)
                        };

                        var client = _httpClientFactory.CreateClient();
                        var request = new HttpRequestMessage(HttpMethod.Post, "https://console.spokes.sh/api/statistics/ping");
                        request.Content = new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json");

                        var response = await client.SendAsync(request, stoppingToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning($"[Telemetry] Admin Console returned {response.StatusCode}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send telemetry ping. This is a soft failure and will be ignored.");
                }

                // Wait 5 days before next ping
                await Task.Delay(TimeSpan.FromDays(5), stoppingToken);
            }
        }
    }
}
