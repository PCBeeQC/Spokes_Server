using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.ReverseProxy;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Extensions;

public class AuthenticationExtensionsTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly EmployeeRepository _employeeRepo;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly OpenIdAccountRepository _openIdRepo;
    private readonly CompanyProfileRepository _companyProfileRepo;
    private readonly SystemConfigRepository _systemConfigRepo;
    private readonly TeamRepository _teamRepo;
    private readonly GlobalKeystoreService _globalKeystore;
    private readonly ServiceProvider _serviceProvider;
    private readonly CookieAuthenticationOptions _cookieOptions;
    private readonly OpenIdConnectOptions _oidcOptions;

    public AuthenticationExtensionsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"Spokes_AuthExtTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _testDir })
            .Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _employeeRepo = new EmployeeRepository(_persistence, config);
        _sessionRepo = new DeviceSessionRepository(_persistence, config);
        _openIdRepo = new OpenIdAccountRepository(_persistence, config);
        _companyProfileRepo = new CompanyProfileRepository(_persistence, config);
        _systemConfigRepo = new SystemConfigRepository(_persistence, config);
        _teamRepo = new TeamRepository(_persistence, config);
        _globalKeystore = new GlobalKeystoreService();

        var mockCore = new Mock<CoreContext>();
        mockCore.SetupGet(c => c.DeviceSessions).Returns(_sessionRepo);
        mockCore.SetupGet(c => c.OpenIdAccounts).Returns(_openIdRepo);
        mockCore.SetupGet(c => c.CompanyProfile).Returns(_companyProfileRepo);
        mockCore.SetupGet(c => c.SystemConfigs).Returns(_systemConfigRepo);

        var mockHr = new Mock<HrContext>();
        mockHr.SetupGet(h => h.Employees).Returns(_employeeRepo);
        mockHr.SetupGet(h => h.Teams).Returns(_teamRepo);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();

        services.AddSingleton(_employeeRepo);
        services.AddSingleton(_sessionRepo);
        services.AddSingleton(_openIdRepo);
        services.AddSingleton(_companyProfileRepo);
        services.AddSingleton(_systemConfigRepo);
        services.AddSingleton(_teamRepo);
        services.AddSingleton(mockCore.Object);
        services.AddSingleton(mockHr.Object);
        services.AddSingleton(_globalKeystore);
        services.AddSingleton<FileTokenService>();
        services.AddScoped<UserService>(sp => new UserService(
            new Mock<AuthenticationStateProvider>().Object,
            _employeeRepo,
            _openIdRepo,
            _companyProfileRepo,
            _teamRepo,
            sp.GetRequiredService<SessionService>()));

        services.AddSpokesAuth();

        _serviceProvider = services.BuildServiceProvider();
        _cookieOptions = _serviceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        _oidcOptions = _serviceProvider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
    }

    private async Task<(CookieValidatePrincipalContext ValidateContext, DefaultHttpContext HttpContext)> ValidateCookiePrincipalAsync(
        IEnumerable<Claim> claims,
        string requestPath = "/")
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };
        httpContext.Request.Path = requestPath;

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            null,
            typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);

        var validateContext = new CookieValidatePrincipalContext(httpContext, scheme, _cookieOptions, ticket);
        await _cookieOptions.Events.OnValidatePrincipal(validateContext);
        return (validateContext, httpContext);
    }

    private async Task<(TokenValidatedContext TokenContext, DefaultHttpContext HttpContext)> ValidateOidcTokenAsync(
        IEnumerable<Claim> oidcClaims)
    {
        using var scope = _serviceProvider.CreateScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(oidcClaims, OpenIdConnectDefaults.AuthenticationScheme));
        var scheme = new AuthenticationScheme(
            OpenIdConnectDefaults.AuthenticationScheme,
            null,
            typeof(OpenIdConnectHandler));

        var tokenContext = new TokenValidatedContext(
            httpContext,
            scheme,
            _oidcOptions,
            principal,
            new AuthenticationProperties());

        await _oidcOptions.Events.OnTokenValidated(tokenContext);
        return (tokenContext, httpContext);
    }

    #region OnValidatePrincipal Security Invariants

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OnValidatePrincipal_MissingOrWhitespaceSessionId_RejectsPrincipal(string? sessionId)
    {
        _employeeRepo.Save(new Employee { Id = "emp-1", FirstName = "Valid", IsActive = true });
        var claims = new List<Claim> { new("EmployeeId", "emp-1") };
        if (sessionId != null)
        {
            claims.Add(new Claim("SessionId", sessionId));
        }

        var (validateCtx, _) = await ValidateCookiePrincipalAsync(claims);

        Assert.Null(validateCtx.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_UnknownOrExpiredDeviceSession_RejectsPrincipal()
    {
        _employeeRepo.Save(new Employee { Id = "emp-exp", FirstName = "Valid", IsActive = true });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-expired",
            EmployeeId = "emp-exp",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var (missingCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", "emp-exp"),
            new Claim("SessionId", "sess-does-not-exist")
        ]);
        Assert.Null(missingCtx.Principal);

        var (expiredCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", "emp-exp"),
            new Claim("SessionId", "sess-expired")
        ]);
        Assert.Null(expiredCtx.Principal);
    }

    [Theory]
    [InlineData("/_blazor", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/spokesapi/chat/channels", true)]
    [InlineData("/hubs/chat", true)]
    [InlineData("/media/chat/ch1/file.png", true)]
    [InlineData("/internal/diagnostics", true)]
    [InlineData("/spokesapi/auth/force-logout", false)]
    [InlineData("/", false)]
    public async Task OnValidatePrincipal_RevokedSession_FlagsIsRevokedAndRejectsProtectedEndpoints(string path, bool shouldRejectPrincipal)
    {
        _employeeRepo.Save(new Employee { Id = "emp-rev", FirstName = "Revoked", IsActive = true });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-rev",
            EmployeeId = "emp-rev",
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });

        var (validateCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", "emp-rev"),
            new Claim("SessionId", "sess-rev")
        ], path);

        Assert.True(validateCtx.Properties.GetParameter<bool>("IsRevoked"));
        if (shouldRejectPrincipal)
        {
            Assert.Null(validateCtx.Principal);
        }
        else
        {
            Assert.NotNull(validateCtx.Principal);
        }
    }

    [Fact]
    public async Task OnValidatePrincipal_UnlinkedPendingPrincipal_EvenWhenSubCollidesWithEmployeeId_RejectsApisAndRedirectsPagesToAccessPending()
    {
        // Regression test: an existing Employee whose Id happens to equal the unlinked OIDC sub
        // must NEVER grant access without an explicit OpenIdAccount linkage.
        const string collidingId = "emp-collision-id";
        _employeeRepo.Save(new Employee
        {
            Id = collidingId,
            FirstName = "Admin",
            IsAdmin = true,
            IsActive = true
        });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-pending",
            EmployeeId = collidingId,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });

        Claim[] pendingClaims =
        [
            new Claim("sub", collidingId),
            new Claim("PendingApproval", "true"),
            new Claim("SessionId", "sess-pending")
        ];

        // 1. Blocked on Blazor circuit & API endpoints
        var (blazorCtx, _) = await ValidateCookiePrincipalAsync(pendingClaims, "/_blazor");
        Assert.Null(blazorCtx.Principal);

        var (apiCtx, _) = await ValidateCookiePrincipalAsync(pendingClaims, "/spokesapi/projects");
        Assert.Null(apiCtx.Principal);

        // 2. Redirected to /access-pending on normal page routes
        var (homeCtx, homeHttp) = await ValidateCookiePrincipalAsync(pendingClaims, "/");
        Assert.NotNull(homeCtx.Principal);
        Assert.Equal(StatusCodes.Status302Found, homeHttp.Response.StatusCode);
        Assert.Equal("/access-pending", homeHttp.Response.Headers.Location.ToString());
        Assert.Equal("/access-pending", homeHttp.Items["Spokes.AuthRedirect"]);

        // 3. Allowed without redirect loop on /access-pending, /logout, and branding logo
        var (pendingPageCtx, pendingHttp) = await ValidateCookiePrincipalAsync(pendingClaims, "/access-pending");
        Assert.NotNull(pendingPageCtx.Principal);
        Assert.Equal(StatusCodes.Status200OK, pendingHttp.Response.StatusCode);
        Assert.False(pendingHttp.Items.ContainsKey("Spokes.AuthRedirect"));

        var (logoCtx, _) = await ValidateCookiePrincipalAsync(pendingClaims, "/spokesapi/system/logo");
        Assert.NotNull(logoCtx.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_UnlinkedPendingPrincipal_UpgradesAutomaticallyOnceOpenIdAccountIsLinked()
    {
        const string sub = "oidc-sub-newly-approved";
        const string employeeId = "emp-approved-1";
        _employeeRepo.Save(new Employee
        {
            Id = employeeId,
            FirstName = "Approved",
            LastName = "Member",
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        });
        _openIdRepo.Save(new OpenIdAccount
        {
            Sub = sub,
            LinkedEmployeeId = employeeId
        });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-upgrade",
            EmployeeId = sub,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });

        var (validateCtx, httpCtx) = await ValidateCookiePrincipalAsync(
        [
            new Claim("sub", sub),
            new Claim("PendingApproval", "true"),
            new Claim("SessionId", "sess-upgrade")
        ], "/");

        Assert.NotNull(validateCtx.Principal);
        Assert.True(validateCtx.ShouldRenew);
        Assert.Equal(employeeId, validateCtx.Principal!.FindFirst("EmployeeId")?.Value);
        Assert.Equal(StatusCodes.Status200OK, httpCtx.Response.StatusCode);
        Assert.Equal(employeeId, _sessionRepo.GetById("sess-upgrade")?.EmployeeId);
    }

    [Fact]
    public async Task OnValidatePrincipal_SystemRenderer_RequiresInternalRendererClaim()
    {
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-render",
            EmployeeId = "system-renderer",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        var (forgedCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", "system-renderer"),
            new Claim("SessionId", "sess-render")
        ], "/projects/p1/quotes/q1");
        Assert.Null(forgedCtx.Principal);

        var (validCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", "system-renderer"),
            new Claim("SessionId", "sess-render"),
            new Claim("Spokes_InternalRenderer", "true")
        ], "/projects/p1/quotes/q1");
        Assert.NotNull(validCtx.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_DeactivatedEmployee_RevokesAllSessionsEvictsKeystoreAndRejectsPrincipal()
    {
        const string employeeId = "emp-deactivated";
        _employeeRepo.Save(new Employee
        {
            Id = employeeId,
            FirstName = "Former",
            IsActive = false
        });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-deact-1",
            EmployeeId = employeeId,
            HasVaultCookie = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });

        var mockCrypto = new Mock<ICryptoService>();
        mockCrypto.Setup(c => c.DeriveKeyFromPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).Returns("kek");
        mockCrypto.Setup(c => c.DecryptAes(It.IsAny<string>(), "kek")).Returns("<RSAKeyValue><Modulus>m</Modulus></RSAKeyValue>");
        Assert.True(_globalKeystore.UnlockVault(employeeId, "enc", "pwd", mockCrypto.Object));

        var (validateCtx, _) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", employeeId),
            new Claim("SessionId", "sess-deact-1")
        ], "/");

        Assert.Null(validateCtx.Principal);
        var updatedSession = _sessionRepo.GetById("sess-deact-1");
        Assert.NotNull(updatedSession?.RevokedAt);
        Assert.False(updatedSession!.HasVaultCookie);
        Assert.False(_globalKeystore.IsUserUnlocked(employeeId));
    }

    [Theory]
    [InlineData("/_blazor", true, false)]
    [InlineData("/spokesapi/chat/channels", true, false)]
    [InlineData("/hubs/chat", true, false)]
    [InlineData("/media/albums/a1/pic.jpg", true, false)]
    [InlineData("/internal/metrics", true, false)]
    [InlineData("/", false, true)]
    [InlineData("/chat", false, true)]
    [InlineData("/banned", false, false)]
    [InlineData("/logout", false, false)]
    [InlineData("/spokesapi/system/logo", false, false)]
    public async Task OnValidatePrincipal_BannedEmployee_EnforcesStrictContainment(
        string path,
        bool expectRejectedPrincipal,
        bool expectRedirectToBanned)
    {
        const string employeeId = "emp-banned-1";
        _employeeRepo.Save(new Employee
        {
            Id = employeeId,
            FirstName = "Banned",
            IsActive = true,
            IsBanned = true
        });
        _sessionRepo.Save(new DeviceSession
        {
            Id = "sess-banned-1",
            EmployeeId = employeeId,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        var (validateCtx, httpCtx) = await ValidateCookiePrincipalAsync(
        [
            new Claim("EmployeeId", employeeId),
            new Claim("SessionId", "sess-banned-1")
        ], path);

        if (expectRejectedPrincipal)
        {
            Assert.Null(validateCtx.Principal);
        }
        else
        {
            Assert.NotNull(validateCtx.Principal);
        }

        if (expectRedirectToBanned)
        {
            Assert.Equal(StatusCodes.Status302Found, httpCtx.Response.StatusCode);
            Assert.Equal("/banned", httpCtx.Response.Headers.Location.ToString());
            Assert.Equal("/banned", httpCtx.Items["Spokes.AuthRedirect"]);
        }
        else
        {
            Assert.False(httpCtx.Items.ContainsKey("Spokes.AuthRedirect"));
        }
    }

    [Fact]
    public async Task OnValidatePrincipal_PreBanPrivilegedPrincipal_DowngradesClaimsDeletesRefreshAndVaultCookiesAndUpgradesWhenUnbanned()
    {
        const string employeeId = "emp-ban-transition";
        const string sessionId = "sess-ban-transition";
        var emp = new Employee
        {
            Id = employeeId,
            FirstName = "Transition",
            LastName = "User",
            Email = "transition@spokes.test",
            IsActive = true,
            IsAdmin = true,
            IsBanned = true,
            Permissions = [AppPermissions.Chat.Use, AppPermissions.Projects.View]
        };
        _employeeRepo.Save(emp);
        _sessionRepo.Save(new DeviceSession
        {
            Id = sessionId,
            EmployeeId = employeeId,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });

        // 1. Pre-ban cookie ticket still carrying Admin role + Permissions + browser refresh/vault cookies
        var httpContext = new DefaultHttpContext { RequestServices = _serviceProvider };
        httpContext.Request.Path = "/";
        httpContext.Request.Headers.Cookie = "Spokes_Refresh=stale_refresh_tok; chat_vault_key=stale_vault_key";

        var preBanPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", employeeId),
            new Claim("EmployeeId", employeeId),
            new Claim("SessionId", sessionId),
            new Claim(ClaimTypes.Role, AppPermissions.Admin.RoleName),
            new Claim("Permission", AppPermissions.Chat.Use)
        ], CookieAuthenticationDefaults.AuthenticationScheme));

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            null,
            typeof(CookieAuthenticationHandler));
        var preBanTicket = new AuthenticationTicket(
            preBanPrincipal,
            new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);

        var banValidateCtx = new CookieValidatePrincipalContext(httpContext, scheme, _cookieOptions, preBanTicket);
        await _cookieOptions.Events.OnValidatePrincipal(banValidateCtx);

        Assert.NotNull(banValidateCtx.Principal);
        Assert.True(banValidateCtx.ShouldRenew);
        Assert.True(banValidateCtx.Principal!.HasClaim("Banned", "true"));
        Assert.Null(banValidateCtx.Principal.FindFirst(ClaimTypes.Role));
        Assert.Empty(banValidateCtx.Principal.FindAll("Permission"));
        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Equal("/banned", httpContext.Response.Headers.Location.ToString());

        var setCookies = httpContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("Spokes_Refresh=;", setCookies);
        Assert.Contains("chat_vault_key=;", setCookies);

        // 2. Subsequent unban upgrades the quarantined Banned=true principal back to full Role & Permissions
        emp.IsBanned = false;
        _employeeRepo.Save(emp);

        var (unbanValidateCtx, unbanHttpCtx) = await ValidateCookiePrincipalAsync(
            banValidateCtx.Principal.Claims,
            "/");

        Assert.NotNull(unbanValidateCtx.Principal);
        Assert.True(unbanValidateCtx.ShouldRenew);
        Assert.False(unbanValidateCtx.Principal!.HasClaim("Banned", "true"));
        Assert.Equal(AppPermissions.Admin.RoleName, unbanValidateCtx.Principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Contains(unbanValidateCtx.Principal.FindAll("Permission"), c => c.Value == AppPermissions.Chat.Use);
        Assert.Equal(StatusCodes.Status200OK, unbanHttpCtx.Response.StatusCode);
        Assert.False(unbanHttpCtx.Items.ContainsKey("Spokes.AuthRedirect"));
    }

    #endregion

    #region OnTokenValidated Security Invariants

    [Fact]
    public async Task OnTokenValidated_BannedEmployee_DoesNotIssueRefreshCookieRedirectsToBannedAndStripsPrivilegedClaims()
    {
        const string employeeId = "emp-oidc-banned";
        const string sub = "sub-oidc-banned";
        _employeeRepo.Save(new Employee
        {
            Id = employeeId,
            FirstName = "Banned",
            LastName = "User",
            Email = "banned@spokes.test",
            IsAdmin = true,
            IsActive = true,
            IsBanned = true,
            Permissions = [AppPermissions.Projects.View, AppPermissions.Chat.Use]
        });
        _openIdRepo.Save(new OpenIdAccount
        {
            Sub = sub,
            LinkedEmployeeId = employeeId
        });

        var (tokenCtx, httpCtx) = await ValidateOidcTokenAsync(
        [
            new Claim("sub", sub),
            new Claim("name", "Banned User"),
            new Claim("email", "banned@spokes.test")
        ]);

        Assert.Equal("/banned", tokenCtx.Properties?.RedirectUri);
        Assert.DoesNotContain("Spokes_Refresh=", httpCtx.Response.Headers.SetCookie.ToString());
        Assert.NotNull(tokenCtx.Principal);
        Assert.Equal(employeeId, tokenCtx.Principal!.FindFirst("EmployeeId")?.Value);
        Assert.True(tokenCtx.Principal.HasClaim("Banned", "true"));
        Assert.Null(tokenCtx.Principal.FindFirst(ClaimTypes.Role));
        Assert.Empty(tokenCtx.Principal.FindAll("Permission"));
    }

    [Fact]
    public async Task OnTokenValidated_UnlinkedSubWithMatchingEmailAndEmailVerifiedTrue_NeverAutoLinksByEmail()
    {
        var profile = _companyProfileRepo.Get();
        profile.AutoCreateEmployeeOnFirstLogin = false;
        _companyProfileRepo.Save(profile);

        _employeeRepo.Save(new Employee
        {
            Id = "emp-existing-admin",
            FirstName = "Victim",
            LastName = "Admin",
            Email = "admin@spokes.test",
            IsAdmin = true,
            IsActive = true
        });

        var (tokenCtx, httpCtx) = await ValidateOidcTokenAsync(
        [
            new Claim("sub", "attacker-oidc-sub"),
            new Claim("name", "Attacker"),
            new Claim("email", "admin@spokes.test"),
            new Claim("email_verified", "true")
        ]);

        Assert.Equal("/access-pending", tokenCtx.Properties?.RedirectUri);
        Assert.DoesNotContain("Spokes_Refresh=", httpCtx.Response.Headers.SetCookie.ToString());
        Assert.NotNull(tokenCtx.Principal);
        Assert.Null(tokenCtx.Principal!.FindFirst("EmployeeId"));
        Assert.True(tokenCtx.Principal.HasClaim("PendingApproval", "true"));
        Assert.True(string.IsNullOrEmpty(_openIdRepo.GetBySub("attacker-oidc-sub")?.LinkedEmployeeId));
    }

    [Fact]
    public async Task OnTokenValidated_DeactivatedEmployee_FailsAuthentication()
    {
        _employeeRepo.Save(new Employee
        {
            Id = "emp-oidc-inactive",
            FirstName = "Inactive",
            IsActive = false
        });
        _openIdRepo.Save(new OpenIdAccount
        {
            Sub = "sub-oidc-inactive",
            LinkedEmployeeId = "emp-oidc-inactive"
        });

        var (tokenCtx, _) = await ValidateOidcTokenAsync(
        [
            new Claim("sub", "sub-oidc-inactive"),
            new Claim("name", "Inactive")
        ]);

        Assert.NotNull(tokenCtx.Result?.Failure);
    }

    #endregion

    #region ReverseProxyExtensions ForwardedHeaders Trust Hardening

    [Fact]
    public void AddSpokesReverseProxy_DefaultConfiguration_ExcludesForwardedHostAndTrustsOnlyLoopback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_systemConfigRepo);
        services.AddSpokesReverseProxy(config);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.False(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.Equal(1, options.ForwardLimit);

        Assert.Contains(IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("127.0.0.42")));

        // Private RFC1918 networks must NOT be trusted unless explicitly configured
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("10.0.0.5")));
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("172.18.0.5")));
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("192.168.1.50")));
    }

    [Fact]
    public void AddSpokesReverseProxy_WithExplicitTrustedProxiesCidrsAndHostOptIn_ConfiguresAndMasksHostBits()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:TrustForwardedHost"] = "true",
                ["ReverseProxy:ForwardLimit"] = "2",
                // Non-zero host bits (172.18.0.1/16) must be masked cleanly without throwing
                ["ReverseProxy:TrustedProxies"] = "172.18.0.1/16, 10.99.0.5",
                ["ReverseProxy:TrustedNetworks:0"] = "192.168.50.123/24"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_systemConfigRepo);
        services.AddSpokesReverseProxy(config);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.Equal(2, options.ForwardLimit);
        Assert.Contains(IPAddress.Parse("10.99.0.5"), options.KnownProxies);
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("172.18.25.40")));
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("192.168.50.9")));
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("172.19.0.1")));
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("192.168.51.1")));
    }

    #endregion
}
