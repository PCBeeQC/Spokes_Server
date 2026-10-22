using System;
using System.Text.Json;

namespace Spokes_Server.Core.Services.Identity;

public record CasdoorInitDataParameters(
    string PublicUrl,
    string BuiltInUsername,
    string HashedBuiltInPassword,
    string SpokesUsername,
    string HashedSpokesPassword,
    string ClientId,
    string ClientSecret,
    string CompanyName,
    string LogoUrl,
    string FaviconUrl,
    string FixedLogoUrl,
    string FixedFaviconUrl);

public class CasdoorConfigTemplateGenerator
{
    public string GenerateAppConf(string publicUrl, string dataPath, string runmode = "prod")
    {
        var dbPath = System.IO.Path.Combine(dataPath, "casdoor.db");
        return $@"appname = casdoor
httpport = 8000
runmode = {runmode}
SessionOn = true
copyrequestbody = true
driverName = sqlite
dataSourceName = file:{dbPath}?cache=shared
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
    }

    public string GenerateInitDataJson(CasdoorInitDataParameters parameters)
    {
        var signinRedirectUri = parameters.PublicUrl.TrimEnd('/') + "/signin-oidc";
        var signoutRedirectUri = parameters.PublicUrl.TrimEnd('/') + "/signout-callback-oidc";
        var mobileLogoutRedirectUri = parameters.PublicUrl.TrimEnd('/') + "/sso/mobile-logout-complete";

        var emptySalt = string.Empty;

        var initData = new
        {
            organizations = new[]
            {
                new { owner = "admin", name = "built-in", displayName = "Built-in", logo = "", favicon = "", hasPrivilegeConsent = false, passwordType = "bcrypt", passwordSalt = emptySalt, passwordOptions = new[] { "AtLeast6" }, languages = new[] { "en", "fr", "es", "de" }, accountItems = new object[] { }, tags = new string[] { }, countryCodes = new string[] { }, defaultApplication = "", websiteUrl = "", defaultAvatar = "" },
                new { owner = "admin", name = "spokes-org", displayName = parameters.CompanyName, logo = parameters.FixedLogoUrl, favicon = parameters.FixedFaviconUrl, hasPrivilegeConsent = true, passwordType = "bcrypt", passwordSalt = emptySalt, passwordOptions = new[] { "AtLeast6" }, languages = new[] { "en", "fr", "es", "de" }, accountItems = new object[] { }, tags = new string[] { }, countryCodes = new string[] { }, defaultApplication = "spokes", websiteUrl = "https://www.spokes.sh", defaultAvatar = "https://cdn.casbin.org/img/casbin.svg" }
            },
            users = new[]
            {
                new {
                    owner = "built-in",
                    name = parameters.BuiltInUsername,
                    id = Guid.NewGuid().ToString(),
                    type = "normal-user",
                    password = parameters.HashedBuiltInPassword,
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
                    name = parameters.SpokesUsername,
                    id = Guid.NewGuid().ToString(),
                    type = "normal-user",
                    password = parameters.HashedSpokesPassword,
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
                    homepageUrl = parameters.PublicUrl,
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
                    displayName = parameters.CompanyName,
                    logo = parameters.LogoUrl,
                    favicon = parameters.FaviconUrl,
                    homepageUrl = parameters.PublicUrl,
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
                    clientId = parameters.ClientId,
                    clientSecret = parameters.ClientSecret
                }
            }
        };

        return JsonSerializer.Serialize(initData, new JsonSerializerOptions { WriteIndented = true });
    }
}
