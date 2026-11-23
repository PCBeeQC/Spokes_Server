using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Filters;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Security;

[Collection("OpenIdConfigurationCache")]
public class SignatureWebmailUploadAndMobileAuthSecurityTests : TestDataTestBase
{
    private const string TestAuthority = "http://auth.example.com";
    private const string TestClientId = "spokes-mobile-app";
    private const string TestClientSecret = "secret123";
    private const string ValidPkceVerifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _config;
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly SessionService _sessionService;
    private readonly UserService _userService;
    private readonly MobileOAuthChallengeService _challengeService;
    private readonly FileTokenService _fileTokenService;
    private readonly ScopedKeystoreService _keystore;
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _securityKey;
    private readonly JsonWebKey _jwk;

    public SignatureWebmailUploadAndMobileAuthSecurityTests()
    {
        var services = new ServiceCollection();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
                ["Spokes_DemoMode"] = "false"
            })
            .Build();

        services.AddSingleton(_config);
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddSingleton(new Mock<AuthenticationStateProvider>().Object);
        services.AddSingleton<EncryptionService>();
        services.AddSingleton<IOpenIdConfigurationProvider, OpenIdConfigurationProvider>();
        services.AddSingleton<MobileOAuthChallengeService>();
        services.AddSingleton<FileTokenService>();
        services.AddSpokesDatabase();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _persistence = _serviceProvider.GetRequiredService<DiskPersistenceService>();
        _sessionService = _serviceProvider.GetRequiredService<SessionService>();
        _userService = _serviceProvider.GetRequiredService<UserService>();
        _challengeService = _serviceProvider.GetRequiredService<MobileOAuthChallengeService>();
        _fileTokenService = _serviceProvider.GetRequiredService<FileTokenService>();
        _keystore = new ScopedKeystoreService(_serviceProvider.GetRequiredService<IDataProtectionProvider>());

        _rsa = RSA.Create(2048);
        _securityKey = new RsaSecurityKey(_rsa) { KeyId = "ws43-security-key" };
        _jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_securityKey);
        _jwk.KeyId = "ws43-security-key";
        _jwk.Use = "sig";
        _jwk.Alg = SecurityAlgorithms.RsaSha256;

        _db.SystemConfigs.Save(new SystemConfig
        {
            Authority = TestAuthority,
            ClientId = TestClientId,
            ClientSecret = TestClientSecret,
            ProviderType = IdpType.External
        });
    }

    public override void Dispose()
    {
        _serviceProvider.GetService<IOpenIdConfigurationProvider>()?.ClearCache();
        _rsa.Dispose();
        _persistence.FlushAll();
        _serviceProvider.Dispose();
        base.Dispose();
    }

    #region Helpers

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Spokes_Server")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Spokes_Mobile")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Spokes repository root from test base directory.");
    }

    private sealed class MutableMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public int CallCount { get; private set; }

        public CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_handler(request));
        }
    }

    private string GenerateIdToken(string sub, string? nonce)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("name", "Security User"),
            new("email", "sec@example.com")
        };
        if (nonce != null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        var token = new JwtSecurityToken(
            issuer: TestAuthority,
            audience: TestClientId,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(_securityKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private MobileAuthController CreateMobileAuthController(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        out CountingHttpMessageHandler countingHandler)
    {
        _serviceProvider.GetService<IOpenIdConfigurationProvider>()?.ClearCache();
        var tracker = new CountingHttpMessageHandler(handler);
        countingHandler = tracker;

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(tracker, disposeHandler: false));

        var controller = new MobileAuthController(
            _db.CoreContext,
            mockFactory.Object,
            _sessionService,
            _userService,
            NullLogger<MobileAuthController>.Instance,
            configurationManager: null,
            oidcOptionsMonitor: null,
            openIdConfigurationProvider: _serviceProvider.GetRequiredService<IOpenIdConfigurationProvider>(),
            challengeService: _challengeService);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
        };

        return controller;
    }

    private Func<HttpRequestMessage, HttpResponseMessage> BuildDefaultIdpHandler(Func<string> idTokenFactory)
    {
        var discoveryDoc = JsonSerializer.Serialize(new
        {
            issuer = TestAuthority,
            authorization_endpoint = $"{TestAuthority}/oauth/authorize",
            token_endpoint = $"{TestAuthority}/oauth/token",
            jwks_uri = $"{TestAuthority}/.well-known/jwks.json"
        });
        var jwksDoc = JsonSerializer.Serialize(new { keys = new[] { _jwk } });

        return req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains(".well-known/openid-configuration", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(discoveryDoc, Encoding.UTF8, "application/json")
                };
            }
            if (uri.Contains(".well-known/jwks.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jwksDoc, Encoding.UTF8, "application/json")
                };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/oauth/token", StringComparison.Ordinal))
            {
                var tokenDoc = JsonSerializer.Serialize(new
                {
                    access_token = "mock_access_token",
                    id_token = idTokenFactory(),
                    token_type = "Bearer",
                    expires_in = 3600
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tokenDoc, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private ClaimsPrincipal SetupAuthenticatedEmployee(Employee emp, string sessionId = "sess-valid")
    {
        _db.Employees.Save(emp);
        _db.DeviceSessions.Save(new DeviceSession { Id = sessionId, EmployeeId = emp.Id });
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", emp.Id),
            new Claim("SessionId", sessionId)
        ], "TestAuth"));
    }

    #endregion

    #region DI Single Public Constructor Invariant

    [Theory]
    [InlineData(typeof(SignatureFileAccessProvider))]
    [InlineData(typeof(UploadRequestGuardFilter))]
    [InlineData(typeof(MobileOAuthChallengeService))]
    [InlineData(typeof(MobileAuthController))]
    public void DiRegisteredTypes_ExposeStrictlyOnePublicConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Single(ctors);
    }

    #endregion

    #region #108 Webmail HTML Sanitization

    [Fact]
    public void EmailContentRendererJs_AlwaysRunsSanitizerInjectsCspAndReferrerPolicyAndFailsClosed()
    {
        var jsPath = Path.Combine(ResolveRepoRoot(), "Spokes_Server", "Spokes_Server", "Components", "Pages", "Email", "EmailContentRenderer.razor.js");
        var source = File.ReadAllText(jsPath);

        Assert.Contains("const result = sanitizeEmailHtml(htmlContent, Boolean(allowRemoteImages));", source);
        Assert.Contains("default-src 'none'", source);
        Assert.Contains("connect-src 'none'", source);
        Assert.Contains("form-action 'none'", source);
        Assert.Contains("referrerMeta.setAttribute('name', 'referrer');", source);
        Assert.Contains("referrerMeta.setAttribute('content', 'no-referrer');", source);
        Assert.Contains("iframe.sandbox = 'allow-same-origin';", source);
        Assert.Contains("iframe.referrerPolicy = 'no-referrer';", source);
        Assert.Contains("return { html: '<!DOCTYPE html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"' + EMAIL_CSP_BLOCK_REMOTE + '\"></head><body></body></html>', blocked: true };", source);
    }

    #endregion

    #region #116 Signature File Access & Migration Scoped Deduplication

    [Fact]
    public void SignatureFileAccessProvider_DeniesWildcardsScopesBusinessAndChecksLegacyDocReferences()
    {
        var owner = new Employee { Id = "emp-signer", IsActive = true };
        var financeOnly = new Employee { Id = "emp-finance", IsActive = true, Permissions = [AppPermissions.Finance.AccessAccounting] };
        var projectsOnly = new Employee { Id = "emp-proj-viewer", IsActive = true, Permissions = [AppPermissions.Projects.View] };
        var bizDocViewer = new Employee { Id = "emp-biz-viewer", IsActive = true, Permissions = [AppPermissions.BusinessDocuments.View] };
        var bizManager = new Employee { Id = "emp-biz-mgr", IsActive = true, Permissions = [AppPermissions.Business.ManageSettings] };
        var restrictedMember = new Employee { Id = "emp-secret-member", IsActive = true, Permissions = [AppPermissions.Projects.View] };

        _db.Projects.Save(new Project
        {
            Id = "proj-secret",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = [restrictedMember.Id]
        });

        _db.ProjectDocuments.Save(new ProjectDocument
        {
            Id = "doc-secret",
            ProjectId = "proj-secret",
            ResolvedBody = "<img src=\"/spokesapi/files/signatures/emp-signer/legacy-sig.png\" />"
        });

        var provider = new SignatureFileAccessProvider(
            _db.ProjectDocuments,
            _db.StandardDocuments,
            _db.Projects,
            _db.Teams);

        Assert.True(provider.CanAccess(owner, owner.Id));
        Assert.False(provider.CanAccess(financeOnly, owner.Id));
        Assert.False(provider.CanAccess(financeOnly, "business"));
        Assert.False(provider.CanUpload(financeOnly, "business"));
        Assert.False(provider.CanAccess(projectsOnly, owner.Id));
        Assert.False(provider.CanAccess(projectsOnly, "business"));

        Assert.True(provider.CanAccess(bizDocViewer, "business"));
        Assert.True(provider.CanUpload(bizDocViewer, "business"));
        Assert.True(provider.CanAccess(bizManager, "business"));
        Assert.True(provider.CanUpload(bizManager, "business"));

        // Private project document reference authorizes only readers of that specific project
        Assert.True(provider.CanAccess(restrictedMember, owner.Id));
        Assert.False(provider.CanAccess(bizDocViewer, owner.Id));

        // Adding a StandardDocument that references the legacy signature opens read to business doc viewers only
        _db.StandardDocuments.Save(new StandardDocument
        {
            Id = "std-1",
            Body = "Signed: /spokesapi/files/signatures/emp-signer/sig2.png"
        });

        Assert.True(provider.CanAccess(bizDocViewer, owner.Id));
        Assert.False(provider.CanAccess(financeOnly, owner.Id));
        Assert.False(provider.CanUpload(bizDocViewer, owner.Id));
    }

    #endregion

    #region NV-4 Filename Sanitization & Narrowed Android FileProvider

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\payload.dll", "payload.dll")]
    [InlineData("report\0evil\r\n.pdf", "reportevil.pdf")]
    [InlineData("..", "attachment")]
    [InlineData(".", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData(null, "attachment")]
    public void SanitizeDisplayFileName_StripsTraversalControlCharsAndEmptyFallbacks(string? raw, string expected)
    {
        Assert.Equal(expected, FileHelper.SanitizeDisplayFileName(raw));
    }

    [Fact]
    public void AndroidFilePathsXml_NarrowsExternalPathToDownloadsSubdirectory()
    {
        var xmlPath = Path.Combine(ResolveRepoRoot(), "Spokes_Mobile", "android", "app", "src", "main", "res", "xml", "file_paths.xml");
        var content = File.ReadAllText(xmlPath);

        Assert.DoesNotContain("path=\".\"", content.Split('\n').First(l => l.Contains("<external-path")));
        Assert.DoesNotContain("path=\"\"", content);
        Assert.Contains("<external-path name=\"my_images\" path=\"Download\" />", content);
    }

    #endregion

    #region NV-5 & NV-6 UploadRequestGuardFilter & FileController Hardening

    private static ResourceExecutingContext CreateUploadFilterContext(
        HttpContext httpContext,
        string category = "chat",
        string contextId = "chan-1")
    {
        var routeData = new RouteData(new RouteValueDictionary
        {
            ["category"] = category,
            ["contextId"] = contextId
        });
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    [Fact]
    public void UploadRequestGuardFilter_HasExpectedOrderAfterDisableRequestSizeLimit()
    {
        var auth = new FileAuthorizationService([], []);
        var filter = new UploadRequestGuardFilter(_userService, auth, _db.CompanyProfile);
        Assert.Equal(1000, filter.Order);
    }

    [Fact]
    public async Task UploadRequestGuardFilter_ShortCircuitsInvalidRequestsBeforeFormBuffering()
    {
        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("chat");
        mockProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "chan-1")).Returns(true);
        mockProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "chan-forbidden")).Returns(false);

        var fileAuth = new FileAuthorizationService([mockProvider.Object], []);
        _db.CompanyProfile.Save(new CompanyProfile { MaxFileUploadSizeBytes = 1024 });

        var mockStorage = new Mock<IStorageHealthService>();
        mockStorage.SetupGet(s => s.IsUploadAllowed).Returns(true);
        mockStorage.SetupGet(s => s.TotalBytes).Returns(10_000_000_000L);
        mockStorage.SetupGet(s => s.AvailableFreeBytes).Returns(StorageHealthService.CriticalThresholdBytes + 200L);

        var filter = new UploadRequestGuardFilter(_userService, fileAuth, _db.CompanyProfile, _fileTokenService, mockStorage.Object, _config);

        var principal = SetupAuthenticatedEmployee(new Employee { Id = "emp-uploader", IsActive = true });

        // 1. Invalid path segment -> 400
        bool nextCalled = false;
        var invalidCtx = CreateUploadFilterContext(new DefaultHttpContext { User = principal }, "chat", "../escape");
        await filter.OnResourceExecutionAsync(invalidCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(invalidCtx, []));
        });
        Assert.False(nextCalled);
        Assert.IsType<BadRequestObjectResult>(invalidCtx.Result);

        // 2. Unauthenticated -> 401
        var unauthCtx = CreateUploadFilterContext(new DefaultHttpContext(), "chat", "chan-1");
        await filter.OnResourceExecutionAsync(unauthCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(unauthCtx, []));
        });
        Assert.False(nextCalled);
        Assert.IsType<UnauthorizedObjectResult>(unauthCtx.Result);

        // 3. Unauthorized context -> 403
        var forbidCtx = CreateUploadFilterContext(new DefaultHttpContext { User = principal }, "chat", "chan-forbidden");
        await filter.OnResourceExecutionAsync(forbidCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(forbidCtx, []));
        });
        Assert.False(nextCalled);
        var forbidObj = Assert.IsType<ObjectResult>(forbidCtx.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidObj.StatusCode);

        // 4. Exceeds available disk budget -> 507
        var diskHttp = new DefaultHttpContext { User = principal };
        diskHttp.Request.ContentLength = 250; // > 200 available budget
        var diskCtx = CreateUploadFilterContext(diskHttp, "chat", "chan-1");
        await filter.OnResourceExecutionAsync(diskCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(diskCtx, []));
        });
        Assert.False(nextCalled);
        var diskObj = Assert.IsType<ObjectResult>(diskCtx.Result);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, diskObj.StatusCode);

        // 5. Exceeds max batch body bytes -> 413 (give huge disk so 507 doesn't trigger first)
        mockStorage.SetupGet(s => s.AvailableFreeBytes).Returns(10_000_000_000L);
        var hugeHttp = new DefaultHttpContext { User = principal };
        hugeHttp.Request.ContentLength = (1024L * FileController.MaxFilesPerUploadBatch) + (4L * 1024L * 1024L) + 1L;
        var hugeCtx = CreateUploadFilterContext(hugeHttp, "chat", "chan-1");
        await filter.OnResourceExecutionAsync(hugeCtx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResourceExecutedContext(hugeCtx, []));
        });
        Assert.False(nextCalled);
        var tooLargeObj = Assert.IsType<ObjectResult>(hugeCtx.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooLargeObj.StatusCode);
    }

    [Fact]
    public async Task UploadRequestGuardFilter_ConfiguresRuntimeBodyAndFormLimitsBeforeNext()
    {
        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("chat");
        mockProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "chan-ok")).Returns(true);

        _db.CompanyProfile.Save(new CompanyProfile { MaxFileUploadSizeBytes = 2048 });
        var filter = new UploadRequestGuardFilter(_userService, new FileAuthorizationService([mockProvider.Object], []), _db.CompanyProfile);
        var principal = SetupAuthenticatedEmployee(new Employee { Id = "emp-ok", IsActive = true });

        var httpContext = new DefaultHttpContext { User = principal };
        var bodyFeature = new MutableMaxRequestBodySizeFeature { MaxRequestBodySize = 100 };
        httpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(bodyFeature);

        var ctx = CreateUploadFilterContext(httpContext, "chat", "chan-ok");
        bool nextInvoked = false;
        await filter.OnResourceExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult(new ResourceExecutedContext(ctx, []));
        });

        Assert.True(nextInvoked);
        long expectedLimit = (2048L * FileController.MaxFilesPerUploadBatch) + (4L * 1024L * 1024L);
        Assert.Equal(expectedLimit, bodyFeature.MaxRequestBodySize);
        Assert.NotNull(httpContext.Features.Get<IFormFeature>());
    }

    private FileController CreateFileControllerForTests(
        ClaimsPrincipal user,
        Mock<IFileService> mockFileService,
        IFileAccessProvider extraProvider)
    {
        var auth = new FileAuthorizationService([extraProvider], []);
        var controller = new FileController(
            mockFileService.Object,
            auth,
            _db.CompanyProfile,
            NullLogger<FileController>.Instance,
            _userService,
            new ImageProcessingService(),
            _db.ChatChannels,
            config: _config,
            fileTokenService: _fileTokenService);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user,
                RequestServices = _serviceProvider
            }
        };
        return controller;
    }

    [Fact]
    public async Task FileController_RejectsOver50BatchAndZeroByteUploads()
    {
        var principal = SetupAuthenticatedEmployee(new Employee { Id = "emp-batch", IsActive = true });
        var mockProvider = new Mock<IFileAccessProvider>();
        mockProvider.Setup(p => p.Category).Returns("projects");
        mockProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "proj-1")).Returns(true);

        var mockFileService = new Mock<IFileService>();
        var controller = CreateFileControllerForTests(principal, mockFileService, mockProvider.Object);

        // > 50 files rejected immediately
        var tooMany = new FormFileCollection();
        for (int i = 0; i < FileController.MaxFilesPerUploadBatch + 1; i++)
        {
            tooMany.Add(new FormFile(new MemoryStream(new byte[] { 1 }), 0, 1, "files", $"f{i}.txt"));
        }
        var batchRes = await controller.UploadFile("projects", "proj-1", tooMany);
        Assert.IsType<BadRequestObjectResult>(batchRes);

        // 0-byte file rejected
        var zeroByte = new FormFileCollection
        {
            new FormFile(Stream.Null, 0, 0, "files", "empty.txt")
        };
        var emptyRes = await controller.UploadFile("projects", "proj-1", zeroByte);
        var badEmpty = Assert.IsType<BadRequestObjectResult>(emptyRes);
        var spokesEmpty = Assert.IsType<SpokesResult>(badEmpty.Value);
        Assert.Contains("empty", spokesEmpty.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileController_GetEmailAttachment_SetsNoStoreAndVaryHeaders()
    {
        var emp = new Employee { Id = "emp-mail", IsActive = true };
        var principal = SetupAuthenticatedEmployee(emp);

        var emailProvider = new Mock<IFileAccessProvider>();
        emailProvider.Setup(p => p.Category).Returns("email");
        emailProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), $"{emp.Id}:msg-1")).Returns(true);

        var attachmentPath = Path.Combine(_testDataPath, "hello.txt");
        File.WriteAllText(attachmentPath, "secret email body");

        var mockFileService = new Mock<IFileService>();
        mockFileService.Setup(f => f.GetEmailAttachmentPhysicalPath(emp.Id, "msg-1", "att-1")).Returns(attachmentPath);
        mockFileService.Setup(f => f.GetContentType(It.IsAny<string>())).Returns("text/plain");

        var controller = CreateFileControllerForTests(principal, mockFileService, emailProvider.Object);
        var result = controller.GetEmailAttachment("msg-1", "att-1", employeeId: emp.Id, fn: "hello.txt");

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("private, no-store", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("Cookie, Authorization", controller.Response.Headers["Vary"].ToString());
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
    }

    #endregion

    #region NV-2 Multi-Server Origin Isolation & Mobile Asset Parity

    [Fact]
    public void MobileOriginIsolation_ConfigJsAndNativeGuardsAreInPlaceWithAssetParity()
    {
        var root = ResolveRepoRoot();

        // 1. capacitor.config.json clears allowNavigation and removes errorPath / removed plugin configs
        var capJson = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "capacitor.config.json"));
        using var doc = JsonDocument.Parse(capJson);
        var serverEl = doc.RootElement.GetProperty("server");
        var allowNav = serverEl.GetProperty("allowNavigation");
        Assert.Equal(JsonValueKind.Array, allowNav.ValueKind);
        Assert.Equal(0, allowNav.GetArrayLength());
        Assert.False(serverEl.TryGetProperty("errorPath", out _));

        // 2. Partitioned storage keys and structurally isolated per-origin Preferences + native shell stores present
        var authJs = File.ReadAllText(Path.Combine(root, "Spokes_Server", "Spokes_Server", "wwwroot", "js", "spokes-auth.js"));
        var vaultJs = File.ReadAllText(Path.Combine(root, "Spokes_Server", "Spokes_Server", "wwwroot", "js", "spokes-vault.js"));
        var androidPlugin = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "java", "com", "pcbee", "spokes", "OriginPreferencesPlugin.java"));
        var androidShellStore = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "java", "com", "pcbee", "spokes", "shell", "SpokesShellStore.kt"));
        var androidOriginSecurity = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "java", "com", "pcbee", "spokes", "SpokesOriginSecurity.java"));
        var iosOriginPrefs = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "OriginPreferencesPlugin.swift"));
        var iosShellStore = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "SpokesShellStore.swift"));
        var iosOriginSecurity = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "SpokesOriginSecurity.swift"));
        var iosNotifPlugin = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "NotificationCryptoPlugin.swift"));
        var iosAppDelegate = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "AppDelegate.swift"));

        Assert.Contains("spokes_refresh::", authJs);
        Assert.Contains("spokes_device_id::", authJs);
        Assert.DoesNotContain("savedServers", authJs);
        Assert.Contains("chat_vault_key::", vaultJs);

        Assert.Contains("public class OriginPreferencesPlugin", androidPlugin);
        Assert.Contains("SpokesShellStore.getOriginPrefs", androidPlugin);
        Assert.Contains("spokes_origin_", androidShellStore);
        Assert.Contains("public final class SpokesOriginSecurity", androidOriginSecurity);
        Assert.Contains("validateReadableSourceUri", androidOriginSecurity);
        Assert.Contains("isSameOriginAsCallingWebView", androidOriginSecurity);

        Assert.Contains("public class OriginPreferencesPlugin", iosOriginPrefs);
        Assert.Contains("SpokesShellStore.originDefaults", iosOriginPrefs);
        Assert.Contains("com.pcbee.spokes.origin.", iosShellStore);
        Assert.Contains("validateReadableSourceFileUrl", iosOriginSecurity);
        Assert.Contains("isSameOriginAsCallingWebView", iosOriginSecurity);
        Assert.Contains("CAPPluginMethod(name: \"setServerBadgeCount\", returnType: CAPPluginReturnPromise)", iosNotifPlugin);
        Assert.DoesNotContain("fetchServerUnreadCount", iosNotifPlugin);

        // Superseded legacy web-shell & ACL files must stay deleted
        Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "java", "com", "pcbee", "spokes", "OriginScopedPreferencesPlugin.java")));
        Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "java", "com", "pcbee", "spokes", "WidgetManagerPlugin.java")));
        Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "NotificationCryptoPlugin.m")));
        Assert.False(File.Exists(Path.Combine(root, "Spokes_Server", "Spokes_Server", "wwwroot", "js", "share-router.js")));

        var iosBridgeManager = File.ReadAllText(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "BridgeManager.swift"));
        Assert.Contains("SpokesOriginSecurity.isAllowedServerNavigationTarget", iosBridgeManager);

        int spokesVcStart = iosAppDelegate.IndexOf("class SpokesViewController", StringComparison.Ordinal);
        Assert.True(spokesVcStart > 0);
        string spokesVcSlice = iosAppDelegate.Substring(spokesVcStart);
        Assert.DoesNotContain("Self.parseDestination", spokesVcSlice);
        Assert.DoesNotContain("SpokesNative_openServer", spokesVcSlice);

        // 3. Mobile www/index.html stub matches Android and iOS copies byte-for-byte, and deleted web-shell assets stay gone
        var srcHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "Spokes_Mobile", "www", "index.html")));
        var androidHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "assets", "public", "index.html")));
        var iosHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "public", "index.html")));
        Assert.Equal(srcHash, androidHash);
        Assert.Equal(srcHash, iosHash);

        foreach (var deletedRel in new[] { "error.html", Path.Combine("js", "switcher.js"), Path.Combine("js", "share-router.js") })
        {
            Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "www", deletedRel)));
            Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "android", "app", "src", "main", "assets", "public", deletedRel)));
            Assert.False(File.Exists(Path.Combine(root, "Spokes_Mobile", "ios", "App", "App", "public", deletedRel)));
        }
    }

    #endregion

    #region NV-7 Mobile OIDC Challenge, State/Nonce Binding & Redirect URI Enforcement

    [Fact]
    public void MobileOAuthChallengeService_EnforcesSingleUseAndDeviceBinding()
    {
        var challenge1 = _challengeService.CreateChallenge("device-alpha");
        Assert.False(string.IsNullOrWhiteSpace(challenge1.State));
        Assert.False(string.IsNullOrWhiteSpace(challenge1.Nonce));

        // Matching device succeeds once and consumes state
        Assert.True(_challengeService.TryConsumeChallenge(challenge1.State, "device-alpha", out var consumedNonce));
        Assert.Equal(challenge1.Nonce, consumedNonce);

        // Replay is rejected
        Assert.False(_challengeService.TryConsumeChallenge(challenge1.State, "device-alpha", out _));

        // Mismatched device is rejected and atomically invalidates the challenge so it cannot be probed/reused
        var challenge2 = _challengeService.CreateChallenge("device-alpha");
        Assert.False(_challengeService.TryConsumeChallenge(challenge2.State, "device-beta", out _));
        Assert.False(_challengeService.TryConsumeChallenge(challenge2.State, "device-alpha", out _));
    }

    [Fact]
    public async Task MobileAuthController_ChallengeThenExchange_EndToEndHappyPathAndReplayBlocked()
    {
        _db.Employees.Save(new Employee
        {
            Id = "emp-oidc",
            FirstName = "Mobile",
            LastName = "Tester",
            Email = "sec@example.com",
            IsActive = true
        });
        _db.OpenIdAccounts.Save(new OpenIdAccount
        {
            Sub = "sub-mobile-1",
            LinkedEmployeeId = "emp-oidc"
        });

        string currentNonce = string.Empty;
        var controller = CreateMobileAuthController(
            BuildDefaultIdpHandler(() => GenerateIdToken("sub-mobile-1", currentNonce)),
            out var handler);

        var challengeRes = controller.CreateChallenge(new MobileAuthController.MobileChallengeRequest { DeviceId = "dev-happy" });
        var challengeOk = Assert.IsType<OkObjectResult>(challengeRes);
        var challengePayload = Assert.IsType<MobileAuthController.MobileChallengeResponse>(challengeOk.Value);
        currentNonce = challengePayload.Nonce;

        var req = new MobileAuthController.MobileExchangeRequest
        {
            Code = "auth_code_1",
            CodeVerifier = ValidPkceVerifier,
            RedirectUri = MobileAuthController.AllowedMobileRedirectUri,
            DeviceId = "dev-happy",
            State = challengePayload.State
        };

        var exchangeRes = await controller.Exchange(req);
        Assert.IsType<OkObjectResult>(exchangeRes);
        Assert.True(handler.CallCount > 0);

        // Replaying the same request must fail before contacting IdP again
        int callsBeforeReplay = handler.CallCount;
        var replayRes = await controller.Exchange(req);
        Assert.IsType<BadRequestObjectResult>(replayRes);
        Assert.Equal(callsBeforeReplay, handler.CallCount);
    }

    [Fact]
    public async Task MobileAuthController_RejectsBadRedirectBadPkceDeviceMismatchBeforeContactingIdp()
    {
        var controller = CreateMobileAuthController(
            BuildDefaultIdpHandler(() => GenerateIdToken("sub-never", "unused")),
            out var handler);

        var c1 = Assert.IsType<OkObjectResult>(controller.CreateChallenge(new MobileAuthController.MobileChallengeRequest { DeviceId = "dev-1" }));
        var p1 = Assert.IsType<MobileAuthController.MobileChallengeResponse>(c1.Value);

        // 1. Disallowed RedirectUri (including legacy spokes://callback) never calls IdP
        var badRedirect = await controller.Exchange(new MobileAuthController.MobileExchangeRequest
        {
            Code = "c",
            CodeVerifier = ValidPkceVerifier,
            RedirectUri = "spokes://callback",
            DeviceId = "dev-1",
            State = p1.State
        });
        Assert.IsType<BadRequestObjectResult>(badRedirect);
        Assert.Equal(0, handler.CallCount);

        // 2. Invalid PKCE CodeVerifier (< 43 chars / invalid chars) never calls IdP
        var badPkce = await controller.Exchange(new MobileAuthController.MobileExchangeRequest
        {
            Code = "c",
            CodeVerifier = "too-short",
            RedirectUri = MobileAuthController.AllowedMobileRedirectUri,
            DeviceId = "dev-1",
            State = p1.State
        });
        Assert.IsType<BadRequestObjectResult>(badPkce);
        Assert.Equal(0, handler.CallCount);

        // 3. Mismatched DeviceId never calls IdP
        var wrongDevice = await controller.Exchange(new MobileAuthController.MobileExchangeRequest
        {
            Code = "c",
            CodeVerifier = ValidPkceVerifier,
            RedirectUri = MobileAuthController.AllowedMobileRedirectUri,
            DeviceId = "dev-attacker",
            State = p1.State
        });
        Assert.IsType<BadRequestObjectResult>(wrongDevice);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-nonce-value")]
    public async Task MobileAuthController_RejectsMissingOrMismatchedJwtNonce(string? tokenNonce)
    {
        var controller = CreateMobileAuthController(
            BuildDefaultIdpHandler(() => GenerateIdToken("sub-nonce", tokenNonce)),
            out _);

        var cOk = Assert.IsType<OkObjectResult>(controller.CreateChallenge(new MobileAuthController.MobileChallengeRequest { DeviceId = "dev-nonce" }));
        var challenge = Assert.IsType<MobileAuthController.MobileChallengeResponse>(cOk.Value);

        var res = await controller.Exchange(new MobileAuthController.MobileExchangeRequest
        {
            Code = "code-nonce",
            CodeVerifier = ValidPkceVerifier,
            RedirectUri = MobileAuthController.AllowedMobileRedirectUri,
            DeviceId = "dev-nonce",
            State = challenge.State
        });

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        var spokes = Assert.IsType<SpokesResult>(bad.Value);
        Assert.False(spokes.IsSuccess);
    }

    #endregion
}
