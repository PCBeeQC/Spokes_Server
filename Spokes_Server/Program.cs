// --- SERVICES BOOTSTRAP ARCHITECTURE ---
// Bootstrap registration code is strictly separated from domain service logic.
// Background and Hosted services (e.g. HostedServices, Queue Consumers) are managed by the Generic Host
// and resolve scoped/transient dependencies using transient service scopes rather than direct static instantiation,
// preventing circular dependency loops and ensuring clean, compile-time safe dependency resolution.
// NOTE ON MONOLITHIC BOOTSTRAPPER (Program.cs): 
// As a self-hosted single-binary monolith, Program.cs intentionally serves as the centralized bootstrapper.
// Retaining middleware configuration (routing, authentication, authorization, reverse proxy/YARP configurations,
// OIDC events, and minimal auth endpoint mappings) in a single file ensures that the strict startup ordering
// required by ASP.NET Core is visible and maintainable in one place, avoiding sequential ordering bugs
// that frequently arise when registering middleware via disparate extension classes.
// 

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Spokes_Server.Components;
using Spokes_Server.Core.Data;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR; // Needed for Employee
using Spokes_Server.Core.Services; // IFileService base
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Constants; // Permissions
using MudBlazor.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Services.Documents;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Security;
using PuppeteerSharp;
using System.Net;
using System.Collections.Concurrent;
using Yarp.ReverseProxy.Transforms;



var builder = WebApplication.CreateBuilder(args);

bool demoMode = builder.Configuration.GetValue<bool>("Spokes_DemoMode");
bool demoSetup = builder.Configuration.GetValue<bool>("Spokes_DemoSetup");

if (demoMode && demoSetup)
{
    throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: Spokes_DemoMode and Spokes_DemoSetup cannot both be true simultaneously. This is a severe security risk. The application will now crash.");
}

// --- DATA PROTECTION ---
// Persist keys to the configured DataPath so they survive container restarts.
// This allows authentication cookies to remain valid.
var dataPath = builder.Configuration["DataPath"] ?? "Data";
Console.WriteLine($"[Startup] Using DataPath: {Path.GetFullPath(dataPath)}");

// --- LOAD SYSTEM CONFIG ---
var settingsDir = Path.Combine(dataPath, "Settings");
if (!Directory.Exists(settingsDir)) Directory.CreateDirectory(settingsDir);
var systemConfigPath = Path.Combine(settingsDir, "system_config.json");
SystemConfig? systemConfig = null;
if (System.IO.File.Exists(systemConfigPath))
{
    try 
    { 
        systemConfig = System.Text.Json.JsonSerializer.Deserialize<SystemConfig>(System.IO.File.ReadAllText(systemConfigPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); 
    } 
    catch (Exception ex) 
    {
        Console.WriteLine($"[Startup] Error loading system config from {systemConfigPath}: {ex.Message}");
    }
}
systemConfig ??= new SystemConfig();

var companyProfilePath = Path.Combine(settingsDir, "company.json");
CompanyProfile? companyProfile = null;
if (System.IO.File.Exists(companyProfilePath))
{
    try 
    { 
        companyProfile = System.Text.Json.JsonSerializer.Deserialize<CompanyProfile>(System.IO.File.ReadAllText(companyProfilePath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); 
    } 
    catch (Exception ex) 
    {
        Console.WriteLine($"[Startup] Error loading company profile from {companyProfilePath}: {ex.Message}");
    }
}
companyProfile ??= new CompanyProfile();

// --- GENERATE LIVEKIT.YAML AND START ---
System.Threading.Tasks.Task.Run(() =>
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var liveKitService = new Spokes_Server.Core.Services.Communication.Voice.LiveKitService(loggerFactory.CreateLogger<Spokes_Server.Core.Services.Communication.Voice.LiveKitService>());
    liveKitService.ApplyPortConfiguration(systemConfig);
});


var keysPath = Path.Combine(dataPath, "keys");
if (!Directory.Exists(keysPath)) Directory.CreateDirectory(keysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Spokes_Server");

// --- SERVICES REGISTRATION ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowPublicApi", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

// Spokes Core Services
builder.Services.AddSpokesCoreServices();
builder.Services.AddSingleton<Spokes_Server.Core.Services.Logging.ISystemLogService, Spokes_Server.Core.Services.Logging.SystemLogService>();
builder.Services.AddScoped<Spokes_Server.Core.Utilities.SpokesDomInteropService>();
builder.Services.AddScoped<Spokes_Server.Core.Services.UI.ImageRecoveryService>();
builder.Services.AddScoped<Spokes_Server.Core.Services.Security.ScopedKeystoreService>();
builder.Services.AddSingleton<Spokes_Server.Core.Services.Security.FileTokenService>();
builder.Services.AddScoped<Spokes_Server.Core.Services.Migrations.LegacyAttachmentMigrationService>();

// 1. MudBlazor (with snackbar position configured)
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomLeft;
    config.SnackbarConfiguration.NewestOnTop = true;
    config.SnackbarConfiguration.ShowTransitionDuration = 200;
    config.SnackbarConfiguration.HideTransitionDuration = 200;
});

builder.Services.AddControllers(options =>
{
    options.Filters.Add<Spokes_Server.Core.Middleware.GlobalExceptionFilter>();
});

var maxFileUploadSize = companyProfile.MaxFileUploadSizeBytes;
var hubMessageSizeLimit = maxFileUploadSize + (50 * 1024 * 1024); // Add 50MB overhead buffer

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxFileUploadSize;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileUploadSize;
});

// 2. Razor Components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
        // Keep idle connections alive for much longer so mobile PWAs can background and resume smoothly
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(24);
    })
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = hubMessageSizeLimit;
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

// 3. Database & Repositories
builder.Services.AddSpokesDatabase();

// 3.5 SignalR for Real-Time Chat
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" });
});
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = hubMessageSizeLimit;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// 4. Authentication
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddMemoryCache();
builder.Services.AddScoped<Spokes_Server.Core.Security.SessionService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>(
    sp => new Spokes_Server.Core.Security.DeviceSessionTicketStore(sp));

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "Smart";
    options.DefaultChallengeScheme = "Smart";
})
.AddCookie(options =>
{
    options.Cookie.Name = "Spokes_Session_v3";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

    // CHANGE THIS: Strict blocks the cookie on the return trip from Authentik
    options.Cookie.SameSite = SameSiteMode.Lax;

    options.ExpireTimeSpan = TimeSpan.FromDays(1);
    options.SlidingExpiration = true;

    // SessionStore is wired below via IPostConfigureOptions to avoid BuildServiceProvider() anti-pattern.
    // BuildServiceProvider() would create a separate singleton Database, causing "Identity missing in session store".
    options.LoginPath = "/sso/login/auto";

    // With ITicketStore, RetrieveAsync already checks revocation and expiry on every request.
    // This handler only needs to signal revocation for downstream logout handling.
    options.Events.OnValidatePrincipal = context =>
    {
        var sessionId = context.Principal?.FindFirst("SessionId")?.Value;
        var employeeId = context.Principal?.FindFirst("EmployeeId")?.Value;
        var db = context.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Aggregate.Database>();

        if (!string.IsNullOrEmpty(employeeId) && employeeId != "system-renderer")
        {
            var employee = db.Employees.GetById(employeeId);
            if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
            {
                context.RejectPrincipal();
                return Task.CompletedTask;
            }
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            var session = db.DeviceSessions.GetById(sessionId);
            if (session != null)
            {
                if (session.RevokedAt != null)
                {
                    context.Properties.SetParameter("IsRevoked", true);
                }
                else
                {
                    var sessionService = context.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
                    sessionService.TouchLastSeen(session);
                }
            }
        }
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/spokesapi") || 
            context.Request.Path.StartsWithSegments("/internal") ||
            context.Request.Path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/spokesapi") || 
            context.Request.Path.StartsWithSegments("/internal") ||
            context.Request.Path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
})
.AddOpenIdConnect(options =>
{
    // If setup is incomplete, inject dummy values so that the authentication middleware does not crash on validation
    var authorityUrl = systemConfig.Authority;
    if (!string.IsNullOrWhiteSpace(authorityUrl) && !authorityUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !authorityUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        authorityUrl = "https://" + authorityUrl;
    }

    if (!string.IsNullOrWhiteSpace(authorityUrl) && !Uri.TryCreate(authorityUrl, UriKind.Absolute, out _))
    {
        Console.WriteLine($"[CRITICAL] Invalid OpenID Authority URL in configuration: '{authorityUrl}'. Falling back to dummy value to prevent crash.");
        authorityUrl = null;
    }

    options.Authority = string.IsNullOrWhiteSpace(authorityUrl) ? "https://dummy.authority.for.setup" : authorityUrl;
    options.RequireHttpsMetadata = false;

    // Use our custom AbsoluteUriDocumentRetriever to fix relative paths returned by some IDPs or misconfigured Casdoor
    HttpMessageHandler handler = systemConfig.ProviderType == Spokes_Server.Core.Models.Core.IdpType.BuiltInCasdoor
        ? (HttpMessageHandler)new CasdoorLoopbackHandler(options.Authority)
        : new HttpClientHandler();

    options.BackchannelHttpHandler = handler;

    var backchannelClient = new HttpClient(handler);
    var documentRetriever = new AbsoluteUriDocumentRetriever(backchannelClient, options.Authority)
    {
        RequireHttps = options.RequireHttpsMetadata
    };

    options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.ConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(
        $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration",
        new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever(),
        documentRetriever
    );

    options.ClientId = string.IsNullOrWhiteSpace(systemConfig.ClientId) ? "dummy_client_id" : systemConfig.ClientId;
    options.ClientSecret = systemConfig.ClientSecret;
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";

    // Allow cookies over HTTP for LAN access
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.NonceCookie.SameSite = SameSiteMode.Lax;

    options.ResponseType = "code";
    options.ResponseMode = "query";
    options.SaveTokens = false; // Disabled to prevent huge cookies causing 502 Bad Gateway
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.TokenValidationParameters.NameClaimType = "name";

    // --- OPENID AUTHENTICATION LOGIC ---
    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<Database>();
            var sysConfig = db.SystemConfigs.Get();
            if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl) && context.ProtocolMessage.RedirectUri != null)
            {
                try
                {
                    var redirectUri = new UriBuilder(context.ProtocolMessage.RedirectUri);
                    var publicUri = new Uri(sysConfig.ServerPublicUrl);

                    redirectUri.Scheme = publicUri.Scheme;
                    redirectUri.Host = publicUri.Host;
                    redirectUri.Port = publicUri.IsDefaultPort ? -1 : publicUri.Port;

                    context.ProtocolMessage.RedirectUri = redirectUri.ToString();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OIDC] Failed to rewrite RedirectUri: {ex.Message}");
                }
            }
            return Task.CompletedTask;
        },
        OnRedirectToIdentityProviderForSignOut = async context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Aggregate.Database>();

            var idToken = context.Properties?.GetParameter<string>("spokes_id_token");
            if (string.IsNullOrEmpty(idToken))
            {
                var sessionId = context.HttpContext.User.FindFirst("SessionId")?.Value;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = db.DeviceSessions.GetAll().FirstOrDefault(s => s.Id == sessionId);
                    if (session != null && !string.IsNullOrEmpty(session.IdToken))
                    {
                        idToken = session.IdToken;
                    }
                }
            }

            if (!string.IsNullOrEmpty(idToken))
            {
                context.ProtocolMessage.IdTokenHint = idToken;
            }

            var sysConfig = db.SystemConfigs.Get();
            if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl) && context.ProtocolMessage.PostLogoutRedirectUri != null)
            {
                try
                {
                    var redirectUri = new UriBuilder(context.ProtocolMessage.PostLogoutRedirectUri);
                    var publicUri = new Uri(sysConfig.ServerPublicUrl);

                    redirectUri.Scheme = publicUri.Scheme;
                    redirectUri.Host = publicUri.Host;
                    redirectUri.Port = publicUri.IsDefaultPort ? -1 : publicUri.Port;
                    context.ProtocolMessage.PostLogoutRedirectUri = redirectUri.ToString();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OIDC] Failed to rewrite PostLogoutRedirectUri: {ex.Message}");
                }
            }

            // To prevent reverse proxy crashes (e.g. 502 Bad Gateway) caused by massive Location headers 
            // from 302 Redirects, we convert the logout request to an auto-submitting POST form.
            context.HandleResponse();
            context.Response.StatusCode = 200;
            context.Response.Headers.Remove("Location");

            var html = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8' />
                    <meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=0'>
                    <title>Logging out...</title>
                    <style>
                        body {{ background-color: #1e1e2d; color: white; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; font-family: 'Roboto', sans-serif; }}
                        .spinner {{ width: 40px; height: 40px; border: 4px solid rgba(255,255,255,0.3); border-top: 4px solid #594ae2; border-radius: 50%; animation: spin 1s linear infinite; margin-bottom: 20px; }}
                        @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
                    </style>
                </head>
                <body>
                    <div style='display:flex;flex-direction:column;align-items:center;text-align:center;padding:40px;'>
                        <div class='spinner'></div>
                        <p>Securely signing out...</p>
                    </div>
                    <form id='logoutForm' method='GET' action='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.IssuerAddress ?? "")}'>
                        <input type='hidden' name='id_token_hint' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.IdTokenHint ?? "")}' />
                        <input type='hidden' name='post_logout_redirect_uri' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.PostLogoutRedirectUri ?? "")}' />
                        <input type='hidden' name='state' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.ProtocolMessage.State ?? "")}' />
                    </form>
                    <script>document.getElementById('logoutForm').submit();</script>
                </body>
                </html>";

            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(html);
        },
        OnRemoteFailure = context =>
        {
            // Specifically handling "Correlation failed" (and other IdP errors) which occur
            // frequently when PWA resumes from background after cookies expire.
            // Silently redirect them back to the auto-login endpoint to try again.
            var redirectUrl = "/sso/login/auto";
            if (context.Properties?.RedirectUri != null)
            {
                redirectUrl += $"?returnUrl={Uri.EscapeDataString(context.Properties.RedirectUri)}&sso_error=1";
            }
            else
            {
                redirectUrl += "?sso_error=1";
            }
            context.Response.Redirect(redirectUrl);
            context.HandleResponse();
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            // Clear the auto-SSO loop-detection cookie on successful login
            context.HttpContext.Response.Cookies.Delete("Spokes_SSO_Attempt");

            var db = context.HttpContext.RequestServices.GetRequiredService<Database>();

            var principal = context.Principal;
            if (principal == null) return;

            var sub = principal.FindFirst("sub")?.Value
                      ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var name = principal.FindFirst("name")?.Value ?? "Unknown User";
            var email = principal.FindFirst("email")?.Value
                        ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                        ?? "";

            if (string.IsNullOrEmpty(sub)) return;

            // 1. Look up or create the OpenIdAccount
            var openIdAccount = db.OpenIdAccounts.GetBySub(sub);
            var isFirstTimeLogin = openIdAccount == null; // Track if this is a brand new OpenID identity

            if (openIdAccount == null)
            {
                // First time seeing this OpenID identity
                openIdAccount = new OpenIdAccount
                {
                    Sub = sub,
                    Name = name,
                    Email = email,
                    FirstSeenAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                };
                db.OpenIdAccounts.Save(openIdAccount);
            }
            else
            {
                // Update last login and sync name/email from IdP
                openIdAccount.LastLoginAt = DateTime.UtcNow;
                openIdAccount.Name = name;
                if (!string.IsNullOrEmpty(email)) openIdAccount.Email = email;
                db.OpenIdAccounts.Save(openIdAccount);
            }

            // 2. Find or create the linked Employee
            Employee? existingUser = null;

            if (!string.IsNullOrEmpty(openIdAccount.LinkedEmployeeId))
            {
                // Account is linked to an employee
                existingUser = db.Employees.GetById(openIdAccount.LinkedEmployeeId);
            }

            if (existingUser == null)
            {
                // Self-healing for legacy users without a LinkedEmployeeId
                existingUser = db.Employees.GetAll().FirstOrDefault(e => e.OidcSub == sub);
                
                if (existingUser != null)
                {
                    openIdAccount.LinkedEmployeeId = existingUser.Id;
                    db.OpenIdAccounts.Save(openIdAccount);
                }
            }

            if (existingUser == null)
            {
                // Only auto-create employee on FIRST TIME login (brand new OpenID identity)
                // If account was unlinked, user must be re-linked by admin
                var profile = db.CompanyProfile.GetAll().FirstOrDefault();
                var autoCreate = profile?.AutoCreateEmployeeOnFirstLogin ?? true;

                if (autoCreate && isFirstTimeLogin)
                {
                    // Create new employee and link the OpenIdAccount
                    var parts = name.Split(' ', 2);
                    var fName = parts.Length > 0 ? parts[0] : name;
                    var lName = parts.Length > 1 ? parts[1] : "";

                    var newEmp = new Employee
                    {
                        FirstName = fName,
                        LastName = lName,
                        Email = email,
                        IsActive = true,
                        IsAdmin = !db.Employees.GetAll().Any(e => e.IsAdmin)
                    };

                    if (!newEmp.IsAdmin)
                    {
                        var cmpProfile = db.CompanyProfile.Get();
                        if (cmpProfile != null && !string.IsNullOrEmpty(cmpProfile.DefaultPermissionGroupId))
                        {
                            var defaultGroup = cmpProfile.PermissionGroups.FirstOrDefault(g => g.Id == cmpProfile.DefaultPermissionGroupId);
                            if (defaultGroup != null)
                            {
                                newEmp.PermissionGroupId = defaultGroup.Id;
                            }
                            else
                            {
                                newEmp.Permissions.Add(AppPermissions.Chat.Use);
                                newEmp.Permissions.Add(AppPermissions.Calendar.View);
                            }
                        }
                        else
                        {
                            newEmp.Permissions.Add(AppPermissions.Chat.Use);
                            newEmp.Permissions.Add(AppPermissions.Calendar.View);
                        }
                    }

                    db.Employees.Save(newEmp);
                    existingUser = newEmp;

                    // Link the OpenIdAccount to the new employee
                    openIdAccount.LinkedEmployeeId = newEmp.Id;
                    db.OpenIdAccounts.Save(openIdAccount);
                }
                // If not first time or auto-create off, user has no employee (access pending)
            }
            else if (string.IsNullOrEmpty(existingUser.Email) && !string.IsNullOrEmpty(email))
            {
                // Self-healing: update email if missing
                existingUser.Email = email;
                db.Employees.Save(existingUser);
            }

            if (existingUser != null && (!existingUser.IsActive || existingUser.IsSuspended || existingUser.IsBanned))
            {
                context.Fail("User account is deactivated, suspended, or banned.");
                return;
            }

            System.Security.Claims.ClaimsIdentity newIdentity;
            var deviceId = context.Properties?.Items.ContainsKey("DeviceId") == true ? context.Properties.Items["DeviceId"] : null;
            var sessionService = context.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();

            if (existingUser != null)
            {
                // Create session using centralized service
                var (session, _) = sessionService.CreateSession(
                    context.HttpContext, existingUser.Id, deviceId,
                    issueCookie: true, idToken: context.TokenEndpointResponse?.IdToken);

                // Build full identity using centralized service
                newIdentity = sessionService.BuildIdentity(existingUser, session.Id);
            }
            else
            {
                // Fallback for brand new unlinked OpenID identities
                newIdentity = new System.Security.Claims.ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
                newIdentity.AddClaim(new System.Security.Claims.Claim("sub", sub));
                newIdentity.AddClaim(new System.Security.Claims.Claim("name", name));
                newIdentity.AddClaim(new System.Security.Claims.Claim("email", email));
                
                var (session, _) = sessionService.CreateSession(
                    context.HttpContext, sub, deviceId,
                    issueCookie: true, idToken: context.TokenEndpointResponse?.IdToken);
                newIdentity.AddClaim(new System.Security.Claims.Claim("SessionId", session.Id));
            }

            // Lock in persistent session cookie so mobile PWA WebViews don't drop the login
            context.Properties ??= new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
            context.Properties.IsPersistent = true;
            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1);



            // Replace the principal to avoid storing all IdP claims (like groups) in the cookie
            // which can cause 502/400 errors from reverse proxies due to header size limits
            context.Principal = new System.Security.Claims.ClaimsPrincipal(newIdentity);

            await Task.CompletedTask;
        }
    };
})
.AddScheme<Spokes_Server.Core.Security.DeviceTokenAuthOptions, Spokes_Server.Core.Security.DeviceTokenAuthHandler>("DeviceToken", null)
.AddPolicyScheme("Smart", "Smart", options =>
{
    // Route each request to the appropriate authentication handler:
    //   - Bearer header → DeviceToken handler (mobile native HTTP)
    //   - Session cookie present → Cookie handler (fast path, most requests)
    //   - Refresh cookie present → DeviceToken handler (desktop silent re-auth)
    //   - Nothing → Cookie handler (will trigger OIDC challenge via LoginPath)
    options.ForwardDefaultSelector = context =>
    {
        var auth = context.Request.Headers.Authorization.FirstOrDefault();
        if (auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return "DeviceToken";

        if (context.Request.Cookies.ContainsKey("Spokes_Session_v3"))
            return CookieAuthenticationDefaults.AuthenticationScheme;

        if (context.Request.Cookies.ContainsKey("Spokes_Refresh"))
            return "DeviceToken";

        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

// Configure Cookie Authentication Options using proper DI to avoid BuildServiceProvider
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<Microsoft.AspNetCore.Authentication.Cookies.ITicketStore>((options, ticketStore) =>
    {
        options.SessionStore = ticketStore;
    });

// 5. Authorization
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AppPermissions.Admin.PolicyName, policy =>
        policy.Requirements.Add(new PermissionRequirement(AppPermissions.Admin.RoleName)));

    // Dynamically register policies for all defined permissions
    foreach (var group in AppPermissions.GetAll())
    {
        foreach (var permission in group.Value)
        {
            options.AddPolicy(permission, policy =>
                policy.Requirements.Add(new PermissionRequirement(permission)));
        }
    }
});

// Configure Forwarded Headers for Reverse Proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    // Clear default loopback-only restrictions so reverse proxies (Docker bridge, Nginx, Traefik, Caddy)
    // can forward X-Forwarded-Proto (https) and X-Forwarded-Host accurately.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCascadingAuthenticationState();

// Map the public facing URL natively into the internal Router Security Middleware
var publicUrl = systemConfig.ServerPublicUrl;
if (!string.IsNullOrEmpty(publicUrl))
{
    try
    {
        var hostOnly = new Uri(publicUrl).Host;
        // We do not restrict AllowedHosts here because it blocks internal cluster traffic (Docker IPs).
        // It's recommended to leave AllowedHosts as '*' and rely on external reverse proxies.
        Console.WriteLine($"[Startup] Internal API bound to SERVER_PUBLIC_URL: {hostOnly}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Failed to parse SERVER_PUBLIC_URL: {ex.Message}");
    }
}

// 6. Casdoor & LiveKit Reverse Proxy

var casdoorUiRoutes = new[] { "login", "signup", "forget", "callback", "organizations", "users", "roles", "permissions", "models", "adapters", "enforcers", "applications", "providers", "resources", "certs", "tokens", "records", "webhooks", "syncers", "swagger" };
var yarpRoutes = new List<Yarp.ReverseProxy.Configuration.RouteConfig>
{
    new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "livekitRtcRoute",
        ClusterId = "livekitCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/rtc/{**catch-all}" }
    },
    new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "livekitTwirpRoute",
        ClusterId = "livekitCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/twirp/{**catch-all}" }
    }
};

if (systemConfig.ProviderType == Spokes_Server.Core.Models.Core.IdpType.BuiltInCasdoor)
{
    yarpRoutes.Add(new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "casdoorRoute_Static",
        ClusterId = "casdoorCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/static/{**catch-all}" }
    });
    yarpRoutes.Add(new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "casdoorRoute_Api",
        ClusterId = "casdoorCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/api/{**catch-all}" }
    });
    yarpRoutes.Add(new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "casdoorRoute_WellKnown",
        ClusterId = "casdoorCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/.well-known/{**catch-all}" }
    });
    
    yarpRoutes.Add(new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "casdoorRoute_AuthCallback",
        ClusterId = "casdoorCluster",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/AuthCallbackHandler.js" }
    });

    foreach (var route in casdoorUiRoutes)
    {
        yarpRoutes.Add(new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = $"casdoorRoute_{route}",
            ClusterId = "casdoorCluster",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = $"/{route}/{{**catch-all}}" }
        });
    }
}

builder.Services.AddReverseProxy()
    .LoadFromMemory(
        yarpRoutes.ToArray(),
        new[] {
            new Yarp.ReverseProxy.Configuration.ClusterConfig
            {
                ClusterId = "casdoorCluster",
                Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "casdoorDest", new Yarp.ReverseProxy.Configuration.DestinationConfig { Address = "http://127.0.0.1:8000" } }
                }
            },
            new Yarp.ReverseProxy.Configuration.ClusterConfig
            {
                ClusterId = "livekitCluster",
                Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "livekitDest", new Yarp.ReverseProxy.Configuration.DestinationConfig { Address = "http://127.0.0.1:7880" } }
                }
            }
        })
    .AddTransforms(builderContext =>
    {
        if (builderContext.Route.ClusterId == "casdoorCluster")
        {
            builderContext.AddRequestTransform(context =>
            {
                var cookies = context.HttpContext.Request.Cookies;
                var filteredCookies = cookies
                    .Where(c => !c.Key.StartsWith("Spokes_", StringComparison.OrdinalIgnoreCase) && 
                                !c.Key.StartsWith(".AspNetCore.", StringComparison.OrdinalIgnoreCase))
                    .Select(c => $"{c.Key}={c.Value}");

                context.ProxyRequest.Headers.Remove("Cookie");

                if (filteredCookies.Any())
                {
                    context.ProxyRequest.Headers.Add("Cookie", string.Join("; ", filteredCookies));
                }

                return ValueTask.CompletedTask;
            });

            builderContext.AddResponseTransform(context =>
            {
                // Prevent Casdoor UI from making Native SSO localhost probe requests
                // which trigger Chrome's 'access other apps and services on this device' prompt.
                context.HttpContext.Response.Headers["Content-Security-Policy"] = "connect-src 'self' https: wss:;";
                return ValueTask.CompletedTask;
            });
        }
    });

builder.Services.AddHostedService<AvatarMigrationService>();
var app = builder.Build();

if (args != null && Array.Exists(args, a => a == "--migrate"))
{
    var migrationDb = app.Services.GetRequiredService<Database>();
    migrationDb.Initialize();
    migrationDb.RunOneTimeMigrations();
    
    // Fix: Wait for the background writer to persist the queued changes
    // Since Kestrel isn't running, the hosted service never started processing the queue.
    var hostedServices = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
    var diskService = hostedServices.OfType<Spokes_Server.Core.Data.DiskPersistenceService>().FirstOrDefault();
    if (diskService != null)
    {
        Console.WriteLine("[Bootloader] Flushing database writes to disk...");
        diskService.FlushAll();
    }
    
    Console.WriteLine("[Bootloader] One-time migrations run successfully. Exiting.");
    return;
}

// --- HTTP REQUEST PIPELINE ---

app.UseResponseCompression();
app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    string? startupId = null;
    string? cTimes = null;

    if (context.Request.Query.TryGetValue("startup_id", out var startupIdQuery))
    {
        var rawStr = startupIdQuery.ToString();
        if (rawStr.Length < 100 && System.Text.RegularExpressions.Regex.IsMatch(rawStr, @"^[a-zA-Z0-9\-_]+$"))
        {
            startupId = rawStr;
            context.Response.Cookies.Append("Spokes_Startup_Id", startupId, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5) });
        }
    }
    else if (context.Request.Cookies.TryGetValue("Spokes_Startup_Id", out var startupIdCookie))
    {
        if (startupIdCookie != null && startupIdCookie.Length < 100 && System.Text.RegularExpressions.Regex.IsMatch(startupIdCookie, @"^[a-zA-Z0-9\-_]+$"))
        {
            startupId = startupIdCookie;
        }
    }

    if (context.Request.Query.TryGetValue("c_times", out var cTimesQuery))
    {
        var rawStr = cTimesQuery.ToString();
        if (rawStr.Length < 500 && System.Text.RegularExpressions.Regex.IsMatch(rawStr, @"^[a-zA-Z0-9,_:\.\-]+$"))
        {
            cTimes = rawStr;
            context.Response.Cookies.Append("Spokes_Client_Times", cTimes, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5) });
        }
    }
    else if (context.Request.Cookies.TryGetValue("Spokes_Client_Times", out var cTimesCookie))
    {
        if (cTimesCookie != null && cTimesCookie.Length < 500 && System.Text.RegularExpressions.Regex.IsMatch(cTimesCookie, @"^[a-zA-Z0-9,_:\.\-]+$"))
        {
            cTimes = cTimesCookie;
        }
    }

    if (!string.IsNullOrEmpty(startupId))
    {
        context.Items["StartupCorrelationId"] = startupId;
    }

    if (!string.IsNullOrEmpty(cTimes))
    {
        context.Items["ClientLocalTimings"] = cTimes;
    }

    if (!string.IsNullOrEmpty(startupId) && (context.Request.Query.ContainsKey("startup_id") || path.StartsWith("/chat") || path.StartsWith("/sso/consume-token")))
    {
        Console.WriteLine($"[STARTUP-DIAGNOSTIC] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} - Request: {context.Request.Method} {path} - StartupCorrelationId: {startupId}");
        if (context.Request.Query.ContainsKey("c_times") && !string.IsNullOrEmpty(cTimes))
        {
            Console.WriteLine($"[STARTUP-DIAGNOSTIC] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} - StartupCorrelationId: {startupId} - Client timings: {cTimes}");
        }
    }

    if (path.StartsWith("/sso/consume-token") || path.StartsWith("/chat") || path.StartsWith("/_blazor") || path.StartsWith("/js/capacitor-init.js") || path.StartsWith("/sso/login/auto"))
    {
        var origin = context.Request.Headers["Origin"].ToString();
        var referer = context.Request.Headers["Referer"].ToString();
        var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        var hasCookie = context.Request.Cookies.ContainsKey("Spokes_Session_v3");
        var isLocalReq = context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || 
                         context.Request.Host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"[DIAGNOSTIC] {context.Request.Method} {path}");
        Console.WriteLine($"[DIAGNOSTIC]   Origin: '{origin}'");
        Console.WriteLine($"[DIAGNOSTIC]   Referer: '{referer}'");
        Console.WriteLine($"[DIAGNOSTIC]   Sec-Fetch-Site: '{secFetchSite}'");
        Console.WriteLine($"[DIAGNOSTIC]   Has Spokes_Session_v3 Cookie: {hasCookie}");
        Console.WriteLine($"[DIAGNOSTIC]   QueryString: {SanitizeDiagnosticQuery(context.Request.Query)}");
    }

    static string SanitizeDiagnosticQuery(IQueryCollection query)
    {
        if (query == null || query.Count == 0) return string.Empty;

        var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "token", "refreshToken", "refresh_token", "code", "id_token", "id_token_hint",
            "password", "secret", "key", "t", "renderToken", "sig", "signature", "access_token"
        };

        var parts = query.Select(kvp =>
            sensitiveKeys.Contains(kvp.Key)
                ? $"{kvp.Key}=[REDACTED]"
                : $"{kvp.Key}={kvp.Value}");

        return "?" + string.Join("&", parts);
    }


    await next(context);
});


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // app.UseHsts(); // Disabled for reverse proxy/http setup
}

// app.UseHttpsRedirection(); // Disabled for reverse proxy/http setup

// Prevent browsers from caching HTML document responses.
// Static assets (JS, CSS, etc.) are fingerprinted by MapStaticAssets() with content-based URLs,
// so they have long cache lifetimes and self-invalidate on content changes.
// But the HTML page itself contains the <ImportMap> with SRI integrity hashes — if a browser
// serves a stale HTML page after a deployment, the old hashes won't match the new JS files,
// and the browser will block them entirely. This forces browsers to always fetch fresh HTML.
// ADDITIONALLY: We use "no-transform" to prevent Cloudflare Auto Minify from modifying JS/CSS
// files in transit, which would break the strict SRI hashes generated by Blazor.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        bool isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;

        // Ensure Blazor pages are never cached even if ContentType is set late
        bool isLikelyPage = string.IsNullOrEmpty(System.IO.Path.GetExtension(path))
                            && !path.StartsWith("/api")
                            && !path.StartsWith("/spokesapi")
                            && !path.StartsWith("/hubs")
                            && !path.StartsWith("/_blazor");

        // 1. Prevent HTML caching so Blazor ImportMaps are always fresh after a server update
        if (isHtml || isLikelyPage)
        {
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate, no-transform";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        // 2. Prevent Cloudflare from altering JS/CSS files (Auto Minify) which breaks Blazor SRI Hashes
        else if (context.Response.ContentType?.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase) == true ||
                 context.Response.ContentType?.StartsWith("text/javascript", StringComparison.OrdinalIgnoreCase) == true ||
                 context.Response.ContentType?.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) == true)
        {
            var cc = context.Response.Headers["Cache-Control"].ToString();
            if (!string.IsNullOrEmpty(cc) && !cc.Contains("no-transform"))
            {
                context.Response.Headers["Cache-Control"] = cc + ", no-transform";
            }
            else if (string.IsNullOrEmpty(cc))
            {
                context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate, no-transform";
            }
        }
        return Task.CompletedTask;
    });

    await next();
});

// TLS preconnect hint for mobile clients — emits a Link header so the browser
// begins DNS+TCP+TLS for the app origin while still receiving the HTML body.
// Android WebView's TLS handshake is ~300ms slower than iOS WKWebView;
// this ensures the connection pool is warm by the time _blazor/negotiate fires.
// Safety: This only appends a response header on text/html responses. It does not
// modify the request, alter routing, touch cookies, or affect authentication.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var ct = context.Response.ContentType;
        if (ct != null && ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            context.Response.Headers.Append("Link", $"<{origin}>; rel=preconnect");
        }
        return Task.CompletedTask;
    });
    await next();
});

// Prevent CDN caching for collocated Blazor JS files since .NET 9 MapStaticAssets
// does not fingerprint their URLs natively, which causes SRI hash mismatches behind Cloudflare.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Request.Path.Value != null && context.Request.Path.Value.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "-1";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.MapStaticAssets();

// --- SETUP REDIRECT MIDDLEWARE ---
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Allow static assets, Setup wizard, and backend APIs
    if (path.StartsWith("/setup") ||
        path.StartsWith("/_blazor") ||
        path.StartsWith("/_framework") ||
        path.StartsWith("/css") ||
        path.StartsWith("/_content") ||
        path.StartsWith("/js") ||
        path.EndsWith(".png") ||
        path.EndsWith(".ico") ||
        path.EndsWith(".svg"))
    {
        await next(context);
        return;
    }

    var db = context.RequestServices.GetRequiredService<Database>();
    var sysConfig = db.SystemConfigs.Get();
    if (!sysConfig.IsSetupComplete)
    {
        // Prevent redirect loops if they are already on /setup
        if (!path.StartsWith("/setup"))
        {
            context.Response.Redirect("/setup");
            return;
        }
    }
    else
    {
        // Setup is complete, block access to the setup page
        if (path.StartsWith("/setup"))
        {
            context.Response.Redirect("/");
            return;
        }

        if (path == "/" || path == "")
        {
            // Clear any pending SSO loop detection cookies so the user has a clean slate
            // if they explicitly land on the root of the app (e.g., Casdoor 3rd party login quirks)
            context.Response.Cookies.Delete("Spokes_SSO_Attempt");
        }
    }

    await next(context);
});

// Authentication & Authorization must be in this order
app.UseCors();

app.Use(async (context, next) =>
{
    // Fix for Casdoor RP-Initiated Logout bug: Casdoor redirects to post_logout_redirect_uri 
    // but drops the `state` parameter. This causes OpenIdConnectHandler to silently swallow 
    // the request and return an empty 200 OK (white page). Since we already cleared the 
    // Spokes cookies in POST /logout, we can safely just bounce the user back to the app root.
    if (context.Request.Path == "/signout-callback-oidc")
    {
        context.Response.Redirect("/");
        return;
    }
    await next();
});

app.UseAuthentication();

// Post-authentication revocation check: OnValidatePrincipal sets IsRevoked
// when a session is revoked server-side. This middleware acts on it.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Force-logout handles revoked session cleanup and mobile/desktop logout redirects; let it execute
    if (path.StartsWith("/spokesapi/auth/force-logout"))
    {
        await next();
        return;
    }

    var authResult = await context.AuthenticateAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    if (authResult?.Properties?.GetParameter<bool>("IsRevoked") == true)
    {
        bool isHtmlPage = !path.StartsWith("/api") &&
                          !path.StartsWith("/spokesapi") &&
                          !path.StartsWith("/internal") &&
                          !path.StartsWith("/hubs") &&
                          !path.StartsWith("/_blazor");
        if (isHtmlPage && context.Request.Method == "GET")
        {
            await context.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
            var redirectUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            try
            {
                await context.SignOutAsync(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = redirectUrl });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RevocationMiddleware] OIDC signout fallback: {ex.Message}");
                context.Response.Redirect("/sso/login");
            }
            return;
        }
        else
        {
            context.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});

// RenderTokenMiddleware MUST run BEFORE UseAuthorization so it can override context.User
// and prevent the AuthorizationMiddleware from triggering an OIDC Challenge redirect to the IDP.
app.UseMiddleware<RenderTokenMiddleware>();

// Mobile PWA / Native Splash Screen Interceptor
// Prevents the heavy Blazor App Shell (MainLayout) from flashing before the native refresh token bootstrap.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
    
    // Only intercept requests to the root or Blazor pages, ignore API, static files, SignalR, etc.
    if (context.Request.Method == "GET" && 
        !path.StartsWith("/api") && 
        !path.StartsWith("/spokesapi") && 
        !path.StartsWith("/_blazor") &&
        !path.StartsWith("/_framework") &&
        !path.StartsWith("/sso") &&
        !path.StartsWith("/mobile-login") &&
        !path.StartsWith("/session-expired") &&
        !path.StartsWith("/login") &&
        !path.StartsWith("/signup") &&
        !path.StartsWith("/forget") &&
        !path.StartsWith("/callback") &&
        !path.Contains('.'))
    {
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        bool isCapacitor = userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase) || context.Request.Query["client"] == "mobile";
        bool isAuthenticated = context.User.Identity?.IsAuthenticated == true;
        
        if (isCapacitor && !isAuthenticated)
        {
            // Intercept and return the native HTML bridge to fetch the refresh token from Preferences natively
            // and perform the POST bootstrap, avoiding the Blazor MainLayout flash entirely.
            // Note: We do NOT delete Spokes_Session_v3 here so existing sliding session cookies are preserved.
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8' />
                    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover'>
                    <style>
                        body { background-color: #1e1e2d; margin: 0; padding: 0; }
                    </style>
                </head>
                <body>
                    <script>
                        (async function() {
                            await new Promise(res => setTimeout(res, 50)); // Wait for Capacitor to inject
                            var hostname = window.location.hostname;
                            var refreshRes = null;
                            try {
                                refreshRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_refresh_' + hostname });
                                if (!refreshRes || !refreshRes.value) {
                                    refreshRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_refresh' });
                                }
                            } catch(e) {}
                            
                            if (refreshRes && refreshRes.value) {
                                var form = document.createElement('form');
                                form.method = 'POST';
                                form.action = '/sso/login/mobile-bootstrap';
                                var rt = document.createElement('input'); rt.type = 'hidden'; rt.name = 'refreshToken'; rt.value = refreshRes.value;
                                form.appendChild(rt);
                                var ru = document.createElement('input'); ru.type = 'hidden'; ru.name = 'returnUrl'; ru.value = window.location.pathname + window.location.search + window.location.hash;
                                form.appendChild(ru);
                                try {
                                    var devRes = await window.Capacitor.Plugins.Preferences.get({ key: 'spokes_device_id' });
                                    var devId = (devRes && devRes.value) || localStorage.getItem('spokes_device_id') || '';
                                    if ((!devId || devId.startsWith('dev_')) && window.Capacitor.Plugins && window.Capacitor.Plugins.NotificationCrypto && window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId) {
                                        var cryptoRes = await window.Capacitor.Plugins.NotificationCrypto.getStableDeviceId();
                                        if (cryptoRes && cryptoRes.deviceId) {
                                            devId = cryptoRes.deviceId;
                                            try { await window.Capacitor.Plugins.Preferences.set({ key: 'spokes_device_id', value: devId }); } catch(e) {}
                                        }
                                    }
                                    if (devId) {
                                        var di = document.createElement('input'); di.type = 'hidden'; di.name = 'deviceId'; di.value = devId;
                                        form.appendChild(di);
                                    }
                                } catch(e) {}
                                document.body.appendChild(form);
                                form.submit();
                                return;
                            }
                            
                            window.location.href = '/sso/login/auto?client=mobile&returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search + window.location.hash);
                        })();
                    </script>
                </body>
                </html>
            ");
            return;
        }
    }
    
    await next();
});

app.UseAuthorization();

app.UseAntiforgery();

// --- ENDPOINTS ---

app.MapGet("/spokesapi/auth/force-logout", async (HttpContext context, Spokes_Server.Aggregate.Database db, [Microsoft.AspNetCore.Mvc.FromQuery] string? client = null) =>
{
    Spokes_Server.Core.Models.Core.DeviceSession? session = null;
    
    // First try the standard Auth context (used by Mobile)
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var sessionId = context.User.FindFirst("SessionId")?.Value;
        if (!string.IsNullOrEmpty(sessionId))
        {
            session = db.DeviceSessions.GetById(sessionId);
        }
    }

    // Fallback to the Refresh token lookup (used by Desktop)
    if (session == null && context.Request.Cookies.TryGetValue("Spokes_Refresh", out var token) && !string.IsNullOrEmpty(token))
    {
        var sessionService = context.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
        var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault() ?? context.Request.Cookies["Spokes_Device"];
        var result = sessionService.ValidateToken(token, deviceId, allowRevoked: true);
        session = result?.Session;
    }

        if (session != null && session.RevokedAt != null)
        {
            context.Response.Cookies.Delete("chat_vault_key", new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax
            });

            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var isMobile = client == "mobile" || userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase);
            if (isMobile)
            {
                var idToken = await context.GetTokenAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, "id_token");
                if (string.IsNullOrEmpty(idToken))
                {
                    var authResult = await context.AuthenticateAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
                    idToken = authResult?.Properties?.GetParameter<string>("spokes_id_token");
                }
                
                if (string.IsNullOrEmpty(idToken))
                {
                    idToken = session?.IdToken;
                }

                if (!string.IsNullOrEmpty(idToken))
                {
                    await context.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

                    var oidcOptions = context.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>()
                        .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);

                    string endSessionUrl;
                    try
                    {
                        if (oidcOptions?.ConfigurationManager != null)
                        {
                            var config = await oidcOptions.ConfigurationManager.GetConfigurationAsync(CancellationToken.None);
                            endSessionUrl = config?.EndSessionEndpoint ?? (oidcOptions.Authority?.TrimEnd('/') + "/api/logout");
                        }
                        else
                        {
                            endSessionUrl = (oidcOptions?.Authority?.TrimEnd('/') ?? "") + "/api/logout";
                        }
                    }
                    catch
                    {
                        endSessionUrl = (oidcOptions?.Authority?.TrimEnd('/') ?? "") + "/api/logout";
                    }

                    var cache = context.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                    var reqId = Guid.NewGuid().ToString("N");
                    cache.Set($"logout_{reqId}", new Tuple<string, string>(idToken ?? "", endSessionUrl ?? ""), TimeSpan.FromMinutes(5));

                    var html = Spokes_Server.Core.Security.MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);
                    await context.Response.WriteAsync(html);
                    return;
                }
            }

            await context.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
            try
            {
                await context.SignOutAsync(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/sso/login" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ForceLogout] OIDC signout fallback: {ex.Message}");
                context.Response.Redirect("/sso/login");
            }
            return;
        }

    // If not revoked, this might be a CSRF attempt, just bounce them to home.
    context.Response.Redirect("/");
});

// Lightweight session priming endpoint for native apps.
// Path "/auth/prime-session" is NOT excluded by the inline SSO middleware's
// path filter (not /spokesapi, /api, /_blazor, /static, /sso, no "."),
// so two things happen when this request arrives:
//
//  1. The native HTTP stack (URLSession/HttpURLConnection) resolves its cookie
//     jar to send the request — this acts as a synchronization barrier after
//     app resume, ensuring cookies are loaded from persistent storage.
//
//  2. If Spokes_Session_v3 is expired but Spokes_Refresh is valid, the inline
//     SSO middleware silently renews the session and the response carries a
//     fresh Set-Cookie header.
//
// Called by ensureCookiesFlushed() on native platforms before Blazor.start().
app.MapGet("/auth/prime-session", [Microsoft.AspNetCore.Authorization.AllowAnonymous] (HttpContext context) =>
{
    return context.User.Identity?.IsAuthenticated == true
        ? Results.Ok()
        : Results.StatusCode(401);
});

// Native mobile session refresh: exchanges a refresh token (from Capacitor Preferences body) for a Spokes_Session_v3 cookie.
// This replaces the cookie-based inline SSO renewal for native apps.
app.MapPost("/spokesapi/auth/refresh", [Microsoft.AspNetCore.Authorization.AllowAnonymous] async (HttpContext context, Spokes_Server.Aggregate.Database db) =>
{
    Spokes_Server.Controllers.MobileRefreshRequest? body;
    try { body = await context.Request.ReadFromJsonAsync<Spokes_Server.Controllers.MobileRefreshRequest>(); }
    catch { return Results.BadRequest(); }
    if (body == null || string.IsNullOrWhiteSpace(body.RefreshToken) || string.IsNullOrWhiteSpace(body.DeviceId)) return Results.BadRequest();

    var sessionService = context.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
    var result = sessionService.ValidateToken(body.RefreshToken, body.DeviceId);
    if (result == null) return Results.Unauthorized();

    Console.WriteLine($"[AUTH-REFRESH] Session refresh successful for: {result.Value.Employee.FullName} (device: {result.Value.Session.DeviceId})");
    await sessionService.SignInAndExtendAsync(context, result.Value.Session, result.Value.Employee);

    // 86400 = 24 hours (matches the sliding expiration in options.ExpireTimeSpan)
    return Results.Ok(new { expiresInSeconds = 86400 });
});

// Allow anonymous access: this endpoint checks the Spokes_Refresh cookie directly against the DB,
// so it doesn't need the session cookie (which may have expired after phone lock).
// This lets the reconnection handler accurately determine if a reload can succeed.
app.MapGet("/spokesapi/auth/current-session", [Microsoft.AspNetCore.Authorization.AllowAnonymous] (HttpContext context, Spokes_Server.Aggregate.Database db) =>
{
    var strict = context.Request.Query.ContainsKey("strict") && context.Request.Query["strict"] == "1";
    if (strict)
    {
        // Only return true if the primary UI session is authenticated. Bypass Spokes_Refresh fallback.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sId = context.User.FindFirst("SessionId")?.Value ?? "active";
            return Results.Ok(new { active = true, sessionId = sId });
        }
        return Results.NotFound();
    }

    var session = Spokes_Server.Core.Security.SessionHelper.GetActiveSession(context, db);
    if (session != null)
    {
        return Results.Ok(new { active = true, sessionId = session.Id });
    }
    return Results.NotFound();
});

// Mobile Bootstrap Endpoint: Securely receives refresh token via POST from Capacitor to avoid client-side cookie race conditions
app.MapPost("/sso/login/mobile-bootstrap", [Microsoft.AspNetCore.Authorization.AllowAnonymous] async (HttpContext httpContext, [FromForm] string? refreshToken, [FromForm] string? deviceId, [FromForm] string? returnUrl) =>
{
    // Guard against Cross-Site Login CSRF (prevent external sites from auto-submitting credentials)
    var requestHost = $"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}";
    if (httpContext.Request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite))
    {
        if (fetchSite == "cross-site" || fetchSite == "same-site")
        {
            return Results.BadRequest();
        }
    }
    if (httpContext.Request.Headers.TryGetValue("Origin", out var origin) && !string.IsNullOrEmpty(origin))
    {
        if (!string.Equals(origin, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest();
        }
    }
    else if (httpContext.Request.Headers.TryGetValue("Referer", out var referer) && !string.IsNullOrEmpty(referer))
    {
        if (!referer.ToString().StartsWith(requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest();
        }
    }

    var target = Spokes_Server.Core.Security.RedirectHelper.GetSafeRedirectUrl(returnUrl, httpContext);

    if (string.IsNullOrEmpty(refreshToken))
    {
        return Results.Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
    }

    var sessionService = httpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
    var result = sessionService.ValidateToken(refreshToken, deviceId);
    
    if (result != null)
    {
        Console.WriteLine($"[SSO-BOOTSTRAP] Mobile bootstrap successful for: {result.Value.Employee.FullName}");
        await sessionService.SignInAndExtendAsync(httpContext, result.Value.Session, result.Value.Employee);
        var encodedTarget = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(target);
        var html = Spokes_Server.Core.Security.MobileSsoHelper.CreateJsRedirectPage(encodedTarget);
        return Results.Content(html, "text/html");
    }

    Console.WriteLine("[SSO-BOOTSTRAP] Mobile bootstrap failed. Token invalid or expired.");
    return Results.Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
}).DisableAntiforgery();

// Auto-Login Endpoint: silently attempts SSO, falls back to login page if session expired
app.MapGet("/sso/login/auto", async (HttpContext httpContext, [FromQuery] string? returnUrl, [FromQuery] string? deviceId, [FromQuery] string? sso_error) =>
{
    Console.WriteLine($"[SSO] /sso/login/auto invoked. returnUrl: {returnUrl}");
    var target = Spokes_Server.Core.Security.RedirectHelper.GetSafeRedirectUrl(returnUrl, httpContext);

    // Check if user is already authenticated using HttpContext directly
    if (httpContext.User.Identity?.IsAuthenticated == true)
    {
        return Results.Redirect(target);
    }

    var db = httpContext.RequestServices.GetRequiredService<Spokes_Server.Aggregate.Database>();
    
    // --- DEMO MODE AUTO-LOGIN ---
    var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
    if (config.GetValue<bool>("Spokes_DemoMode") && httpContext.Request.Query["admin_login"] != "1")
    {
        var demoConfig = db.DemoConfigs.Get();
        if (demoConfig.AutoLoginEmployeeIds.Any())
        {
            var random = new Random();
            var employeeId = demoConfig.AutoLoginEmployeeIds[random.Next(demoConfig.AutoLoginEmployeeIds.Count)];
            var employee = db.Employees.GetById(employeeId);
            
            if (employee != null && employee.IsActive && !employee.IsSuspended && !employee.IsBanned)
            {
                var devId = deviceId ?? httpContext.Request.Headers["X-Device-Id"].FirstOrDefault() ?? httpContext.Request.Cookies["Spokes_Device"];
                
                string? rawToken;
                var deviceSession = Spokes_Server.Core.Security.SessionHelper.IssueRefreshToken(
                    httpContext, db, employee.Id, out rawToken, devId, issueCookie: true);
                
                var sessionService = httpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
                await sessionService.SignInAndExtendAsync(httpContext, deviceSession, employee);


                return Results.Redirect(target);
            }
        }
    }
    // --- END DEMO MODE AUTO-LOGIN ---

    // --- REFRESH TOKEN AUTO-RENEWAL ---
    if (httpContext.Request.Cookies.TryGetValue("Spokes_Refresh", out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
    {
        Console.WriteLine("[SSO] Found Spokes_Refresh cookie. Attempting silent renewal...");
        var sessionService = httpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
        var reqDeviceId = deviceId ?? httpContext.Request.Headers["X-Device-Id"].FirstOrDefault() ?? httpContext.Request.Cookies["Spokes_Device"];
        var result = sessionService.ValidateToken(refreshToken, reqDeviceId);

        if (result != null)
        {
            var (session, employee) = result.Value;
            Console.WriteLine($"[SSO] Silent renewal successful for: {employee.FullName}");

            await sessionService.SignInAndExtendAsync(httpContext, session, employee);

            var refreshCookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(90)
            };
            httpContext.Response.Cookies.Append("Spokes_Refresh", refreshToken, refreshCookieOptions);

            httpContext.Response.Cookies.Delete("Spokes_SSO_Attempt");
            return Results.Redirect(target);
        }

        var refreshDeleteCookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        };
        httpContext.Response.Cookies.Delete("Spokes_Refresh", refreshDeleteCookieOptions);
        Console.WriteLine("[SSO] Silent renewal failed. Token invalid or expired.");
    }
    // --- END REFRESH TOKEN AUTO-RENEWAL ---

    // Intercept Mobile PWA cold starts and proxy them to the System Browser securely
    var userAgent = httpContext.Request.Headers["User-Agent"].ToString();
    var isNativeBrowser = httpContext.Request.Query.ContainsKey("native_browser");
    var isMobileFlow = !isNativeBrowser && (target.Contains("client=mobile", StringComparison.OrdinalIgnoreCase) 
        || userAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase));
        
    // Loop detection: if we already attempted SSO and ended up back here,
    // the SSO session is expired — fall back to the manual login page.
    if (httpContext.Request.Cookies.ContainsKey("Spokes_SSO_Attempt"))
    {
        httpContext.Response.Cookies.Delete("Spokes_SSO_Attempt");
        if (isMobileFlow)
        {
            // If they are in a loop, prevent MobileLogin from automatically bouncing them back
            return Results.Redirect($"/mobile-login?no_auto=1&returnUrl={Uri.EscapeDataString(target)}");
        }
        return Results.Redirect("/session-expired");
    }

    if (isMobileFlow)
    {
        Console.WriteLine("[SSO] Detected mobile client. Redirecting to Blazor /mobile-login page.");
        if (sso_error == "1")
        {
            return Results.Redirect($"/mobile-login?sso_error=1&returnUrl={Uri.EscapeDataString(target)}");
        }
        return Results.Redirect($"/mobile-login?returnUrl={Uri.EscapeDataString(target)}");
    }

    // Set a short-lived cookie to detect redirect loops
    httpContext.Response.Cookies.Append("Spokes_SSO_Attempt", "1", new CookieOptions
    {
        HttpOnly = true,
        MaxAge = TimeSpan.FromMinutes(5),
        SameSite = SameSiteMode.Lax,
        Secure = httpContext.Request.IsHttps
    });

    // Issue the OIDC challenge — if SSO session is valid, user gets logged in silently
    var challengeProps = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
    {
        RedirectUri = target,
        IsPersistent = true
    };
    if (!string.IsNullOrEmpty(deviceId))
        challengeProps.Items["DeviceId"] = deviceId;

    return Results.Challenge(challengeProps, authenticationSchemes: [OpenIdConnectDefaults.AuthenticationScheme]);
});

// Login Endpoint (manual SSO button)
app.MapGet("/sso/login", async (HttpContext httpContext, string? returnUrl, [FromQuery] string? deviceId) =>
{
    var url = Spokes_Server.Core.Security.RedirectHelper.GetSafeRedirectUrl(returnUrl, httpContext);

    // --- DEMO MODE AUTO-LOGIN ---
    var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
    if (config.GetValue<bool>("Spokes_DemoMode") && httpContext.Request.Query["admin_login"] != "1")
    {
        var db = httpContext.RequestServices.GetRequiredService<Spokes_Server.Aggregate.Database>();
        var demoConfig = db.DemoConfigs.Get();
        if (demoConfig.AutoLoginEmployeeIds.Any())
        {
            var random = new Random();
            var employeeId = demoConfig.AutoLoginEmployeeIds[random.Next(demoConfig.AutoLoginEmployeeIds.Count)];
            var employee = db.Employees.GetById(employeeId);
            
            if (employee != null && employee.IsActive && !employee.IsSuspended && !employee.IsBanned)
            {
                var devId = deviceId ?? httpContext.Request.Headers["X-Device-Id"].FirstOrDefault() ?? httpContext.Request.Cookies["Spokes_Device"];
                
                string? rawToken;
                var deviceSession = Spokes_Server.Core.Security.SessionHelper.IssueRefreshToken(
                    httpContext, db, employee.Id, out rawToken, devId, issueCookie: true);
                
                var sessionService = httpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Security.SessionService>();
                await sessionService.SignInAndExtendAsync(httpContext, deviceSession, employee);


                var encodedUrl = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(url);
                var html = Spokes_Server.Core.Security.MobileSsoHelper.CreateJsRedirectPage(encodedUrl);
                return Results.Content(html, "text/html");
            }
        }
    }
    // --- END DEMO MODE AUTO-LOGIN ---

    var props = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
    {
        RedirectUri = url,
        IsPersistent = true
    };
    if (!string.IsNullOrEmpty(deviceId))
        props.Items["DeviceId"] = deviceId;

    return Results.Challenge(props, authenticationSchemes: [OpenIdConnectDefaults.AuthenticationScheme]);
});


// POST form bridge for mobile native browser (safely passes massive tokens via body)
app.MapGet("/sso/mobile-logout-post", (HttpContext context, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string reqId) =>
{
    var key = $"logout_{reqId}";
    if (cache.TryGetValue<Tuple<string, string>>(key, out var data) && data != null)
    {
        cache.Remove(key); // single use
        string idToken = data.Item1;
        string endSessionUrl = data.Item2;

        var serverBase = $"{context.Request.Scheme}://{context.Request.Host}";
        var postLogoutRedirect = $"{serverBase}/sso/mobile-logout-complete";

        var html = $@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset='utf-8' />
                <meta name='viewport' content='width=device-width, initial-scale=1, maximum-scale=1, user-scalable=0'>
                <title>Logging out...</title>
                <style>
                    body {{ background-color: #1e1e2d; color: white; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; font-family: 'Roboto', sans-serif; }}
                    .spinner {{ width: 40px; height: 40px; border: 4px solid rgba(255,255,255,0.3); border-top: 4px solid #594ae2; border-radius: 50%; animation: spin 1s linear infinite; margin-bottom: 20px; }}
                    @keyframes spin {{ 0% {{ transform: rotate(0deg); }} 100% {{ transform: rotate(360deg); }} }}
                </style>
            </head>
            <body>
                <div style='display:flex;flex-direction:column;align-items:center;text-align:center;padding:40px;'>
                    <div class='spinner'></div>
                    <p>Securely signing out...</p>
                </div>
                <form id='logoutForm' method='GET' action='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(endSessionUrl ?? "")}'>
                    <input type='hidden' name='id_token_hint' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(idToken)}' />
                    <input type='hidden' name='post_logout_redirect_uri' value='{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(postLogoutRedirect)}' />
                </form>
                <script>document.getElementById('logoutForm').submit();</script>
            </body>
            </html>";
        return Results.Content(html, "text/html");
    }

    return Results.BadRequest("Invalid or expired logout request.");
});

// OIDC RP-Initiated Logout: post_logout_redirect_uri for mobile clients.
// After the IdP destroys the session, it redirects here. The 302 to spokes://
// is intercepted by ASWebAuthenticationSession (iOS) / AuthTabIntent (Android),
// which auto-dismisses the auth browser and resolves the native plugin's Promise.
app.MapGet("/sso/mobile-logout-complete", () => Results.Redirect("spokes://logout-complete"));

// Logout Endpoint
app.MapPost("/logout", async (HttpContext context) =>
{
    var isMobile = false;
    if (context.Request.HasFormContentType)
    {
        var form = await context.Request.ReadFormAsync();
        isMobile = form["client"].ToString().Contains("mobile", StringComparison.OrdinalIgnoreCase);
    }
    
    // Clear any pending SSO loop detection cookies so the user doesn't get bounced to the session-expired page when they next launch the app
    context.Response.Cookies.Delete("Spokes_SSO_Attempt");

    // 1. Revoke the persistent DeviceSession and delete the Spokes_Refresh cookie
    var db = context.RequestServices.GetRequiredService<Spokes_Server.Aggregate.Database>();
    Spokes_Server.Core.Models.Core.DeviceSession? activeSession = Spokes_Server.Core.Security.SessionHelper.GetActiveSession(context, db);
    if (activeSession != null)
    {
        activeSession.RevokedAt = DateTime.UtcNow;
        db.DeviceSessions.Save(activeSession);

        // Cleanup any linked push subscriptions to instantly stop push notifications to this device
        if (activeSession.HasPush)
        {
            db.DeviceSessions.ClearPushFields(activeSession);
        }
    }

    if (context.Request.Cookies.ContainsKey("Spokes_Refresh"))
    {
        var refreshCookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        };
        context.Response.Cookies.Delete("Spokes_Refresh", refreshCookieOptions);
    }

    // 2. Extract the id_token to check if we can actually log out of the IdP (Casdoor requires it)
    var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var idToken = authResult?.Properties?.GetTokenValue("id_token") ?? authResult?.Properties?.GetParameter<string>("spokes_id_token");
    
    if (string.IsNullOrEmpty(idToken) && activeSession != null && !string.IsNullOrEmpty(activeSession.IdToken))
    {
        idToken = activeSession.IdToken;
    }

    Console.WriteLine($"[SSO] id_token present for IdP logout: {!string.IsNullOrEmpty(idToken)}");

    // 3. If id_token is missing (e.g., silent renewal session), we CANNOT log out of Casdoor.
    // Skip the OIDC redirect entirely and just log out locally.
    if (string.IsNullOrEmpty(idToken))
    {
        Console.WriteLine("[SSO] Skipping IdP logout because id_token is missing. Redirecting locally.");
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (isMobile)
        {
            var serverBaseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var htmlRedirect = $@"
                <!DOCTYPE html>
                <html>
                <body>
                    <script>
                        window._ssoAttempts = 0;
                        document.cookie = 'Spokes_Refresh=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                        window.location.href = '{serverBaseUrl}?client=mobile&cb=' + Date.now();
                    </script>
                </body>
                </html>
            ";
            return Results.Content(htmlRedirect, "text/html");
        }
        return Results.Redirect("/sso/login");
    }

    // 4. We HAVE an id_token, so we can proceed with full global IdP logout
    if (isMobile)
    {
        Console.WriteLine("[SSO] Mobile client logout detected. Performing IdP logout via system browser.");

        // Clear the local cookie immediately
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Construct the IdP end_session URL
        var oidcOptions = context.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        string endSessionUrl;
        try
        {
            var config = await oidcOptions.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
            endSessionUrl = config.EndSessionEndpoint;
        }
        catch
        {
            // Fallback: construct from authority
            endSessionUrl = oidcOptions.Authority?.TrimEnd('/') + "/api/logout";
        }

        var cache = context.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var reqId = Guid.NewGuid().ToString("N");
        cache.Set($"logout_{reqId}", new Tuple<string, string>(idToken ?? "", endSessionUrl ?? ""), TimeSpan.FromMinutes(5));

        var html = Spokes_Server.Core.Security.MobileSsoHelper.CreateMobileLogoutPageAsync(context, reqId);
        return Results.Content(html, "text/html");
    }

    Console.WriteLine("[SSO] Standard client logout detected. Performing global OIDC sign-out.");
    return Results.SignOut(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
        authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
    );
// Caddy On-Demand TLS Integration
});

app.MapGet("/api/tls/ask", [Microsoft.AspNetCore.Authorization.AllowAnonymous] (string? domain, Spokes_Server.Aggregate.Database db, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(domain)) return Results.StatusCode(403);
    
    var config = db.SystemConfigs.Get();
    if (string.IsNullOrWhiteSpace(config.ServerPublicUrl)) return Results.StatusCode(403);

    // Strip protocols for clean comparison
    var expectedDomain = config.ServerPublicUrl.Replace("https://", "").Replace("http://", "").TrimEnd('/');
    var requestedDomain = domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');

    if (expectedDomain.Equals(requestedDomain, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok();
    }
    
    return Results.StatusCode(403);
});

// Controllers
app.MapControllers();

// YARP Reverse Proxy
app.MapReverseProxy();

// SignalR Hub for Chat
app.MapHub<ChatHub>("/hubs/chat");

// Blazor Components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// --- BOOTLOADER ---
// Load data from disk into RAM
var db = app.Services.GetRequiredService<Database>();
db.Initialize();

var escrowService = app.Services.GetRequiredService<ServerEscrowService>();
escrowService.Initialize();

var presenceState = app.Services.GetRequiredService<Spokes_Server.Core.Services.Communication.Presence.PresenceStateService>();
presenceState.InitializeFromSessions(db.DeviceSessions.GetAll());

// Auto-generate missing secrets on boot
var activeProfile = db.CompanyProfile.Get();
bool profileChanged = false;

if (string.IsNullOrEmpty(activeProfile.VapidPublicKey))
{
    var keys = WebPushService.GenerateVapidKeys();
    activeProfile.VapidPublicKey = keys.publicKey;
    activeProfile.VapidPrivateKey = keys.privateKey;
    if (string.IsNullOrEmpty(activeProfile.VapidSubject))
    {
        activeProfile.VapidSubject = "mailto:admin@localhost";
    }
    profileChanged = true;
    Console.WriteLine("[Bootloader] Auto-generated VAPID keys for push notifications.");
}

var envMasterPassword = Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
if (string.IsNullOrEmpty(envMasterPassword) && string.IsNullOrEmpty(activeProfile.PublicChannelMasterPassword))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[Bootloader] CRITICAL ERROR: The master password is not configured.");
    Console.WriteLine("[Bootloader] Please set the SPOKES_MASTER_PASSWORD environment variable to a secure value in your docker-compose.yml and restart the server.");
    Console.ResetColor();
    Environment.Exit(1);
}

if (profileChanged)
{
    db.CompanyProfile.Save(activeProfile);
}

// --- DATA MIGRATION: Sent → Final ---
{
    var migrated = 0;
    foreach (var q in db.Quotes.GetAll().Where(q => q.Status == "Sent"))
    {
        q.Status = "Final";
        db.Quotes.Save(q);
        migrated++;
    }
    foreach (var inv in db.Invoices.GetAll().Where(i => i.Status == "Sent"))
    {
        inv.Status = "Final";
        db.Invoices.Save(inv);
        migrated++;
    }
    foreach (var doc in db.ProjectDocuments.GetAll().Where(d => d.Status == "Sent"))
    {
        doc.Status = "Final";
        db.ProjectDocuments.Save(doc);
        migrated++;
    }
    if (migrated > 0)
        Console.WriteLine($"[Migration] Updated {migrated} document(s) from 'Sent' to 'Final'.");
}

// --- LOCALIZATION ---
var profile = db.CompanyProfile.Get();
if (!string.IsNullOrEmpty(profile?.CurrencySymbol))
{
    var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
    culture.NumberFormat.CurrencySymbol = profile.CurrencySymbol;
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
}

// --- PUPPETEER HEADLESS BROWSER ---
_ = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("[Bootloader] Verifying Chromium for PDF generation in background...");
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();
        Console.WriteLine("[Bootloader] Chromium ready.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bootloader] Failed to download Chromium: {ex.Message}");
    }
});

if (systemConfig.IsSetupComplete && systemConfig.ProviderType == IdpType.BuiltInCasdoor)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(5000); // Give Casdoor time to start
        try
        {
            using var scope = app.Services.CreateScope();
            var casdoorService = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Services.Core.CasdoorProvisioningService>();
            await casdoorService.SyncBrandingAsync();
            Console.WriteLine("[Bootloader] Casdoor branding synced automatically on boot.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bootloader] Failed to sync Casdoor branding on boot: {ex.Message}");
        }
    });
}

// Run Legacy Attachment Migration in Background
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var migrationService = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Services.Migrations.LegacyAttachmentMigrationService>();
        await migrationService.MigrateLegacyBase64AttachmentsAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Migration] Legacy attachment migration failed: {ex.Message}");
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    var presenceState = app.Services.GetRequiredService<Spokes_Server.Core.Services.Communication.Presence.PresenceStateService>();
    presenceState.FlushActiveSessionsToDatabase();

    var hostedServices = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
    var diskService = hostedServices.OfType<Spokes_Server.Core.Data.DiskPersistenceService>().FirstOrDefault();
    if (diskService != null)
    {
        Console.WriteLine("[Shutdown] Flushing database writes to disk...");
        diskService.FlushAll();
    }
});

app.Run();

public partial class Program { }

public class CasdoorLoopbackHandler : DelegatingHandler
{
    private readonly string _authority;
    public CasdoorLoopbackHandler(string authority)
    {
        _authority = authority.TrimEnd('/');
        InnerHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Unconditionally rewrite all backchannel requests to the local Casdoor instance.
        // This handler is only used when ProviderType == BuiltInCasdoor, so every request
        // through it should reach Casdoor at 127.0.0.1:8000 regardless of the URL scheme
        // or host advertised in the OIDC discovery document.
        if (request.RequestUri != null)
        {
            var builder = new UriBuilder(request.RequestUri)
            {
                Scheme = "http",
                Host = "127.0.0.1",
                Port = 8000
            };
            request.RequestUri = builder.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}

public class AbsoluteUriDocumentRetriever : Microsoft.IdentityModel.Protocols.IDocumentRetriever
{
    private readonly Microsoft.IdentityModel.Protocols.HttpDocumentRetriever _inner;
    private readonly string _authority;

    public AbsoluteUriDocumentRetriever(System.Net.Http.HttpClient backchannel, string authority)
    {
        _inner = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever(backchannel);
        _authority = authority.TrimEnd('/');
    }

    public bool RequireHttps
    {
        get => _inner.RequireHttps;
        set => _inner.RequireHttps = value;
    }

    public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (address.StartsWith("/"))
        {
            address = _authority + address;
        }
        else if (address.StartsWith("http://127.0.0.1:8000"))
        {
            address = _authority + address.Substring("http://127.0.0.1:8000".Length);
        }
        else if (address.StartsWith("http://localhost:8000"))
        {
            address = _authority + address.Substring("http://localhost:8000".Length);
        }
        else if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var slashIdx = address.IndexOf('/');
            if (slashIdx >= 0)
            {
                address = _authority + address.Substring(slashIdx);
            }
        }

        return _inner.GetDocumentAsync(address, cancel);
    }
}
