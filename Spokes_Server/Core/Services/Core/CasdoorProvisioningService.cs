using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Services.Core;

public class CasdoorProvisioningService
{
    private readonly Database _db;
    private readonly string _dataPath;
    private readonly IConfiguration _configuration;

    public CasdoorProvisioningService(Database db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
        _dataPath = _configuration["DataPath"] ?? "Data";
    }

    public async Task<(string BuiltInPassword, string SpokesPassword, string ClientId, string ClientSecret)> ProvisionAsync(string publicUrl, string builtInUsername, string builtInPassword, string spokesUsername, string spokesPassword)
    {
        // Casdoor lowercases usernames on insert (isUsernameLowered=true in app.conf).
        // We must match that in init_data.json to avoid UNIQUE constraint crashes on restart.
        builtInUsername = builtInUsername.ToLowerInvariant();
        spokesUsername = spokesUsername.ToLowerInvariant();

        var hashedBuiltInPassword = BCrypt.Net.BCrypt.HashPassword(builtInPassword);
        var hashedSpokesPassword = BCrypt.Net.BCrypt.HashPassword(spokesPassword);

        var persistentCasdoorDir = Path.Combine(_dataPath, "casdoor");
        var persistentConfDir = Path.Combine(persistentCasdoorDir, "conf");
        if (!Directory.Exists(persistentConfDir)) Directory.CreateDirectory(persistentConfDir);

        var ephemeralCasdoorDir = "/app/casdoor";
        var ephemeralConfDir = Path.Combine(ephemeralCasdoorDir, "conf");
        if (!Directory.Exists(ephemeralConfDir)) Directory.CreateDirectory(ephemeralConfDir);

        // 1. Generate app.conf
        var appConf = $@"appname = casdoor
httpport = 8000
runmode = dev
SessionOn = true
copyrequestbody = true
driverName = sqlite
dataSourceName = file:{Path.Combine(_dataPath, "casdoor.db")}?cache=shared
dbName = casdoor
tableNamePrefix =
showSql = false
redisEndpoint =
defaultStorageProvider = 
isCloudIntranet = false
authState = ""casdoor""
socks5Proxy = ""127.0.0.1:10808""
verificationCodeTimeout = 10
initScore = 2000
logPostOnly = true
origin = ""{publicUrl}""
staticBaseUrl = ""https://cdn.casbin.org""
isDemoMode = false
batchSize = 100
enableErrorMask = false
enableSqlCipher = false
enableXssAttack = false
inactiveTimeoutMinutes =
initDataFile = ""./init_data.json""
initDataNewOnly = true
isUsernameLowered = true
";
        await File.WriteAllTextAsync(Path.Combine(persistentConfDir, "app.conf"), appConf);
        await File.WriteAllTextAsync(Path.Combine(ephemeralConfDir, "app.conf"), appConf);

        // 2. Generate Application Credentials
        var clientId = GenerateRandomString(20);
        var clientSecret = GenerateRandomString(40);

        var signinRedirectUri = publicUrl.TrimEnd('/') + "/signin-oidc";
        var signoutRedirectUri = publicUrl.TrimEnd('/') + "/signout-callback-oidc";
        var mobileLogoutRedirectUri = publicUrl.TrimEnd('/') + "/sso/mobile-logout-complete";

        var profile = _db.CompanyProfile.Get();
        var sysConfig = _db.SystemConfigs.Get();
        var companyName = string.IsNullOrWhiteSpace(profile?.CompanyName) ? "Spokes" : profile.CompanyName;
        var logoUrl = $"{publicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/logo.png";
        var faviconUrl = $"{publicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/icon.png";

        var fixedLogoUrl = $"{publicUrl.TrimEnd('/')}/spokes-org-logo.png";
        var fixedFaviconUrl = $"{publicUrl.TrimEnd('/')}/spokes-org-icon.png";

        var emptySalt = string.Empty;

        // 3. Generate init_data.json
        var initData = new
        {
            organizations = new[]
            {
                new { owner = "admin", name = "built-in", displayName = "Built-in", logo = "", favicon = "", hasPrivilegeConsent = false, passwordType = "bcrypt", passwordSalt = emptySalt, passwordOptions = new[] { "AtLeast6" }, languages = new[] { "en", "fr", "es", "de" }, accountItems = new object[] { }, tags = new string[] { }, countryCodes = new string[] { }, defaultApplication = "", websiteUrl = "", defaultAvatar = "" },
                new { owner = "admin", name = "spokes-org", displayName = companyName, logo = fixedLogoUrl, favicon = fixedFaviconUrl, hasPrivilegeConsent = true, passwordType = "bcrypt", passwordSalt = emptySalt, passwordOptions = new[] { "AtLeast6" }, languages = new[] { "en", "fr", "es", "de" }, accountItems = new object[] { }, tags = new string[] { }, countryCodes = new string[] { }, defaultApplication = "spokes", websiteUrl = "https://www.spokes.sh", defaultAvatar = "https://cdn.casbin.org/img/casbin.svg" }
            },
            users = new[]
            {
                new {
                    owner = "built-in",
                    name = builtInUsername,
                    id = Guid.NewGuid().ToString(),
                    type = "normal-user",
                    password = hashedBuiltInPassword,
                    passwordSalt = emptySalt,
                    passwordType = "bcrypt",
                    displayName = "Admin",
                    isAdmin = true,
                    isGlobalAdmin = true,
                    isForbidden = false,
                    isDeleted = false,
                    signupApplication = "app-built-in",
                    address = new string[] { },
                    addresses = new string[] { },
                    groups = new string[] { }
                },
                new {
                    owner = "spokes-org",
                    name = spokesUsername,
                    id = Guid.NewGuid().ToString(),
                    type = "normal-user",
                    password = hashedSpokesPassword,
                    passwordSalt = emptySalt,
                    passwordType = "bcrypt",
                    displayName = "Admin",
                    isAdmin = true,
                    isGlobalAdmin = false,
                    isForbidden = false,
                    isDeleted = false,
                    signupApplication = "spokes",
                    address = new string[] { },
                    addresses = new string[] { },
                    groups = new string[] { }
                }
            },
            applications = new object[]
            {
                new {
                    owner = "admin",
                    name = "app-built-in",
                    displayName = "Built-in",
                    logo = "",
                    favicon = "",
                    homepageUrl = publicUrl,
                    description = "Built-in Application",
                    organization = "built-in",
                    enablePassword = true,
                    enableSignUp = false,
                    enableSigninSession = true,
                    enableAutoSignin = false,
                    enableSilentSignin = false,
                    enableCodeSignin = false,
                    providers = new[]
                    {
                        new { name = "provider_captcha_default", canSignUp = false, canSignIn = false, canUnlink = false, prompted = false, alertType = "None" }
                    },
                    signinMethods = new[]
                    {
                        new { name = "Password", displayName = "Password", rule = "All" }
                    },
                    signupItems = new[]
                    {
                        new { name = "Username", visible = true, required = true, prompted = false, rule = "None" },
                        new { name = "Password", visible = true, required = true, prompted = false, rule = "None" }
                    },
                    grantTypes = new[] { "authorization_code", "password", "client_credentials", "token", "id_token", "refresh_token" },
                    tokenFormat = "JWT",
                    tokenFields = new string[] { },
                    redirectUris = new string[] { },
                    expireInHours = 168
                },
                new {
                    owner = "admin",
                    name = "spokes",
                    displayName = companyName,
                    logo = logoUrl,
                    favicon = faviconUrl,
                    homepageUrl = publicUrl,
                    organization = "spokes-org",
                    enablePassword = true,
                    enableSignUp = true,
                    enableSigninSession = true,
                    enableAutoSignin = true,
                    enableSilentSignin = true,
                    providers = new[]
                    {
                        new { name = "provider_captcha_default", canSignUp = false, canSignIn = false, canUnlink = false, prompted = false, alertType = "None" }
                    },
                    signinMethods = new[]
                    {
                        new { name = "Password", displayName = "Password", rule = "All" }
                    },
                    signupItems = new[]
                    {
                        new { name = "Username", visible = true, required = true, prompted = false, rule = "None" },
                        new { name = "Password", visible = true, required = true, prompted = false, rule = "None" },
                        new { name = "Invitation code", visible = true, required = true, prompted = false, rule = "None" }
                    },
                    grantTypes = new[] { "authorization_code", "password", "client_credentials", "token", "id_token", "refresh_token" },
                    tokenFormat = "JWT",
                    tokenFields = new string[] { },
                    redirectUris = new[] { signinRedirectUri, signoutRedirectUri, mobileLogoutRedirectUri, "spokes://auth-callback" },
                    expireInHours = 168,
                    clientId = clientId,
                    clientSecret = clientSecret
                }
            }
        };

        var initJson = JsonSerializer.Serialize(initData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(persistentCasdoorDir, "init_data.json"), initJson);
        await File.WriteAllTextAsync(Path.Combine(ephemeralCasdoorDir, "init_data.json"), initJson);

        // 4. Update System Config
        var config = _db.SystemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.Authority = publicUrl.TrimEnd('/');
        config.ClientId = clientId;
        config.ClientSecret = clientSecret;
        config.IsSetupComplete = true;
        _db.SystemConfigs.Save(config);

        // 4.5 Create indicator file for entrypoint.sh
        await File.WriteAllTextAsync(Path.Combine(_dataPath, "casdoor.enabled"), "true");

        // 5. Start Casdoor via Supervisor
        try
        {
            var psi = new ProcessStartInfo("supervisorctl", "start casdoor")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                Console.WriteLine($"[Casdoor Provisioning] Supervisor start output: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.WriteLine($"[Casdoor Provisioning] Supervisor start error: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Casdoor Provisioning] Failed to start supervisorctl: {ex.Message}");
        }

        // 6. Forcefully update the built-in admin password via SQLite
        // Wait 3 seconds to ensure Casdoor has finished its InitDatabase sequence
        await Task.Delay(3000);
        try
        {
            var dbPath = Path.Combine(_dataPath, "casdoor.db");
            if (File.Exists(dbPath))
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "UPDATE `user` SET password = $password, password_type = 'bcrypt', password_salt = $salt WHERE name = $adminName AND owner = 'built-in'";
                command.Parameters.AddWithValue("$password", hashedBuiltInPassword);
                command.Parameters.AddWithValue("$adminName", builtInUsername);
                command.Parameters.AddWithValue("$salt", string.Empty);

                var rows = await command.ExecuteNonQueryAsync();

                var command2 = connection.CreateCommand();
                command2.CommandText = "UPDATE `user` SET password = $password, password_type = 'bcrypt', password_salt = $salt WHERE name = $adminName AND owner = 'spokes-org'";
                command2.Parameters.AddWithValue("$password", hashedSpokesPassword);
                command2.Parameters.AddWithValue("$adminName", spokesUsername);
                command2.Parameters.AddWithValue("$salt", string.Empty);

                rows += await command2.ExecuteNonQueryAsync();
                Console.WriteLine($"[Casdoor Provisioning] Updated {rows} admin user(s) with custom password.");

                var patchAppCommand = connection.CreateCommand();
                patchAppCommand.CommandText = "UPDATE `application` SET enable_sign_up = 0, display_name = $cmpName, logo = $logoUrl, favicon = $faviconUrl, enable_auto_signin = 1 WHERE name = 'app-built-in'";
                patchAppCommand.Parameters.AddWithValue("$cmpName", "Casdoor Admin");
                patchAppCommand.Parameters.AddWithValue("$logoUrl", logoUrl);
                patchAppCommand.Parameters.AddWithValue("$faviconUrl", faviconUrl);
                var patched = await patchAppCommand.ExecuteNonQueryAsync();

                var patchSpokesAppCommand = connection.CreateCommand();
                patchSpokesAppCommand.CommandText = "UPDATE `application` SET enable_sign_up = 0, display_name = $cmpName, logo = $logoUrl, favicon = $faviconUrl, enable_auto_signin = 1, client_id = $clientId, client_secret = $clientSecret WHERE name = 'spokes'";
                patchSpokesAppCommand.Parameters.AddWithValue("$cmpName", companyName);
                patchSpokesAppCommand.Parameters.AddWithValue("$logoUrl", logoUrl);
                patchSpokesAppCommand.Parameters.AddWithValue("$faviconUrl", faviconUrl);
                patchSpokesAppCommand.Parameters.AddWithValue("$clientId", clientId);
                patchSpokesAppCommand.Parameters.AddWithValue("$clientSecret", clientSecret);
                patched += await patchSpokesAppCommand.ExecuteNonQueryAsync();

                var patchOrgCommand = connection.CreateCommand();
                patchOrgCommand.CommandText = "UPDATE `organization` SET has_privilege_consent = 1, logo = $fixedLogo, favicon = $fixedFavicon WHERE name = 'spokes-org'";
                patchOrgCommand.Parameters.AddWithValue("$fixedLogo", fixedLogoUrl);
                patchOrgCommand.Parameters.AddWithValue("$fixedFavicon", fixedFaviconUrl);
                await patchOrgCommand.ExecuteNonQueryAsync();

                Console.WriteLine($"[Casdoor Provisioning] Patched {patched} default application(s) with custom branding.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Casdoor Provisioning] Failed to update admin password in SQLite: {ex.Message}");
        }

        return (builtInPassword, spokesPassword, clientId, clientSecret);
    }

    private string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private async Task SyncAppConfOriginAsync(string newPublicUrl)
    {
        var persistentConfDir = Path.Combine(_dataPath, "casdoor", "conf");
        if (!Directory.Exists(persistentConfDir)) Directory.CreateDirectory(persistentConfDir);

        var ephemeralConfDir = "/app/casdoor/conf";
        if (!Directory.Exists(ephemeralConfDir)) Directory.CreateDirectory(ephemeralConfDir);

        var appConf = $@"appname = casdoor
httpport = 8000
runmode = dev
SessionOn = true
copyrequestbody = true
driverName = sqlite
dataSourceName = file:{Path.Combine(_dataPath, "casdoor.db")}?cache=shared
dbName = casdoor
tableNamePrefix =
showSql = false
redisEndpoint =
defaultStorageProvider = 
isCloudIntranet = false
authState = ""casdoor""
socks5Proxy = ""127.0.0.1:10808""
verificationCodeTimeout = 10
initScore = 2000
logPostOnly = true
origin = ""{newPublicUrl}""
staticBaseUrl = ""https://cdn.casbin.org""
isDemoMode = false
batchSize = 100
enableErrorMask = false
enableSqlCipher = false
enableXssAttack = false
inactiveTimeoutMinutes =
initDataFile = ""./init_data.json""
initDataNewOnly = true
isUsernameLowered = true
";
        await File.WriteAllTextAsync(Path.Combine(persistentConfDir, "app.conf"), appConf);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(ephemeralConfDir, "app.conf"), appConf);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Casdoor Sync] Could not write ephemeral app.conf: {ex.Message}");
        }

        Console.WriteLine($"[Casdoor Sync] app.conf origin updated to '{newPublicUrl}'.");
    }

    public async Task SyncBrandingAsync()
    {
        var profile = _db.CompanyProfile.Get();
        var sysConfig = _db.SystemConfigs.Get();
        if (sysConfig.ProviderType != IdpType.BuiltInCasdoor) return;

        // Sync app.conf origin with current ServerPublicUrl
        if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl))
        {
            await SyncAppConfOriginAsync(sysConfig.ServerPublicUrl.TrimEnd('/'));
        }

        var companyName = string.IsNullOrWhiteSpace(profile?.CompanyName) ? "Spokes" : profile.CompanyName;
        var logoUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/logo.png";
        var faviconUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/icon.png";

        var fixedLogoUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/spokes-org-logo.png";
        var fixedFaviconUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/spokes-org-icon.png";

        var clientId = sysConfig.ClientId;
        var clientSecret = sysConfig.ClientSecret;

        // 1. Sync Spokes Application Branding (Always via API)
        try
        {
            using var http = new HttpClient();
            http.BaseAddress = new Uri("http://127.0.0.1:8000");

            var getRes = await http.GetAsync($"/api/get-application?id=admin/spokes&clientId={clientId}&clientSecret={clientSecret}");
            if (getRes.IsSuccessStatusCode)
            {
                var jsonStr = await getRes.Content.ReadAsStringAsync();
                var root = JsonNode.Parse(jsonStr);
                var data = root?["data"];
                if (data != null)
                {
                    data["displayName"] = companyName;
                    data["logo"] = logoUrl;
                    data["favicon"] = faviconUrl;

                    var signinRedirectUri = sysConfig.ServerPublicUrl.TrimEnd('/') + "/signin-oidc";
                    var signoutRedirectUri = sysConfig.ServerPublicUrl.TrimEnd('/') + "/signout-callback-oidc";
                    var mobileLogoutRedirectUri = sysConfig.ServerPublicUrl.TrimEnd('/') + "/sso/mobile-logout-complete";
                    data["redirectUris"] = new JsonArray { signinRedirectUri, signoutRedirectUri, mobileLogoutRedirectUri, "spokes://auth-callback" };

                    data["clientId"] = clientId;
                    data["clientSecret"] = clientSecret;

                    var content = new StringContent(data.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    await http.PostAsync($"/api/update-application?id=admin/spokes&clientId={clientId}&clientSecret={clientSecret}", content);
                }

                // Casdoor is healthy — remove init_data.json so it's never re-processed on restart.
                // The data is already in casdoor.db; the JSON file is redundant after first boot.
                var persistentInitData = Path.Combine(_dataPath, "casdoor", "init_data.json");
                var ephemeralInitData = "/app/casdoor/init_data.json";
                if (File.Exists(persistentInitData))
                {
                    File.Delete(persistentInitData);
                    Console.WriteLine("[Casdoor Sync] Removed persistent init_data.json (no longer needed after first boot).");
                }
                if (File.Exists(ephemeralInitData))
                {
                    File.Delete(ephemeralInitData);
                    Console.WriteLine("[Casdoor Sync] Removed ephemeral init_data.json.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Casdoor Sync] Failed to sync application via API: {ex.Message}");
        }

        // 2. Sync Organization (One-Time Only via DB)
        if (!sysConfig.IsCasdoorSanitized)
        {
            var dbPath = Path.Combine(_dataPath, "casdoor.db");
            if (!File.Exists(dbPath)) return;

            // Retry loop to avoid SQLITE_BUSY locks during Casdoor startup
            int maxRetries = 10;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    await connection.OpenAsync();
                    var getOrgCmd = connection.CreateCommand();
                    getOrgCmd.CommandText = "SELECT account_items, nav_items, widget_items FROM `organization` WHERE name = 'spokes-org'";
                    using var reader = await getOrgCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var accountItemsStr = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                        var navItemsStr = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                        var widgetItemsStr = reader.IsDBNull(2) ? "[]" : reader.GetString(2);

                        var accountItems = JsonNode.Parse(accountItemsStr)?.AsArray();
                        var navItems = JsonNode.Parse(navItemsStr)?.AsArray();
                        var widgetItems = JsonNode.Parse(widgetItemsStr)?.AsArray();

                        // Account Items - Disable everything except essential fields
                        if (accountItems != null)
                        {
                            var keepItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                "Organization", "ID", "Name", "Display name",
                                "User type", "Password", "Email", "Is admin", "Avatar"
                            };

                            foreach (var item in accountItems)
                            {
                                var itemName = item?["name"]?.ToString();
                                if (itemName != null && !keepItems.Contains(itemName))
                                {
                                    item["visible"] = false;
                                }
                            }
                        }

                        // Explicitly define standard Nav Items using exact paths
                        var standardNavItems = new[] {
                            "/home-top", "/orgs-top", "/applications-top", "/roles-top", "/sessions-top",
                            "/admin-top", "/", "/shortcuts", "/apps", "/organizations", "/groups", "/users",
                            "/invitations", "/applications", "/providers", "/resources", "/certs", "/roles",
                            "/permissions", "/models", "/adapters", "/enforcers", "/sessions", "/records",
                            "/tokens", "/verifications", "/sysinfo", "/syncers", "/webhooks", "/webhook-events", "/swagger"
                        };
                        navItems = new JsonArray();
                        foreach (var navItem in standardNavItems)
                        {
                            navItems.Add(navItem);
                        }

                        // Explicitly define standard Widget Items using exact paths
                        var standardWidgetItems = new[] {
                            "tour", "language", "theme"
                        };
                        widgetItems = new JsonArray();
                        foreach (var widgetItem in standardWidgetItems)
                        {
                            widgetItems.Add(widgetItem);
                        }

                        var updateOrgCmd = connection.CreateCommand();
                        updateOrgCmd.CommandText = "UPDATE `organization` SET display_name = $displayName, logo = $logo, favicon = $favicon, has_privilege_consent = 1, website_url = 'https://www.spokes.sh', default_avatar = 'https://cdn.casbin.org/img/casbin.svg', account_items = $accountItems, nav_items = $navItems, widget_items = $widgetItems WHERE name = 'spokes-org'";
                        updateOrgCmd.Parameters.AddWithValue("$displayName", companyName);
                        updateOrgCmd.Parameters.AddWithValue("$logo", fixedLogoUrl);
                        updateOrgCmd.Parameters.AddWithValue("$favicon", fixedFaviconUrl);
                        updateOrgCmd.Parameters.AddWithValue("$accountItems", accountItems?.ToJsonString() ?? "[]");
                        updateOrgCmd.Parameters.AddWithValue("$navItems", navItems?.ToJsonString() ?? "[]");
                        updateOrgCmd.Parameters.AddWithValue("$widgetItems", widgetItems?.ToJsonString() ?? "[]");

                        await updateOrgCmd.ExecuteNonQueryAsync();

                        // Force enable signups on the Spokes application during one-time init
                        var updateAppCmd = connection.CreateCommand();
                        updateAppCmd.CommandText = "UPDATE `application` SET enable_sign_up = 1 WHERE name = 'spokes'";
                        await updateAppCmd.ExecuteNonQueryAsync();

                        sysConfig.IsCasdoorSanitized = true;
                        _db.SystemConfigs.Save(sysConfig);
                        Console.WriteLine("[Casdoor Sync] First-run sanitization applied to Casdoor UI elements.");
                    }

                    // Successfully applied, exit the retry loop
                    break;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
                {
                    if (i == maxRetries - 1)
                    {
                        Console.WriteLine($"[Casdoor Sync] Failed to sync database due to SQLITE_BUSY after {maxRetries} retries.");
                    }
                    else
                    {
                        await Task.Delay(1000); // wait 1s before retrying
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Casdoor Sync] Failed to sync database directly: {ex.Message}");
                    break; // unrecoverable error, break loop
                }
            }
        }
    }
}
