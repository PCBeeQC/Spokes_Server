using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Core.ReverseProxy;
using Spokes_Server.Core.Security;

var builder = WebApplication.CreateBuilder(args);

bool demoMode = builder.Configuration.GetValue<bool>("Spokes_DemoMode");
bool demoSetup = builder.Configuration.GetValue<bool>("Spokes_DemoSetup");

if (demoMode && demoSetup)
{
    throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: Spokes_DemoMode and Spokes_DemoSetup cannot both be true simultaneously. This is a severe security risk. The application will now crash.");
}

// --- DATA PROTECTION & INITIAL CONFIG ---
var dataPath = builder.Configuration["DataPath"] ?? "Data";
Console.WriteLine($"[Startup] Using DataPath: {Path.GetFullPath(dataPath)}");

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
    options.AddPolicy("AllowPublicApi", corsBuilder =>
    {
        corsBuilder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
    });
});

// Modular Spokes Subsystem Wiring
builder.Services.AddSpokesConfiguration(builder.Configuration);
builder.Services.AddSpokesDatabase();
builder.Services.AddSpokesCoreServices();
builder.Services.AddSpokesWebUi(builder.WebHost);
builder.Services.AddSpokesAuth();
builder.Services.AddSpokesReverseProxy();

var app = builder.Build();

// CLI Migration Mode
if (args.Contains("--migrate"))
{
    await app.Services.InitializeSpokesServerAsync();
    
    var hostedServices = app.Services.GetServices<IHostedService>();
    var diskService = hostedServices.OfType<DiskPersistenceService>().FirstOrDefault();
    if (diskService != null)
    {
        Console.WriteLine("[Bootloader] Flushing database writes to disk...");
        await diskService.FlushAllAsync();
    }
    
    Console.WriteLine("[Bootloader] One-time migrations run successfully. Exiting.");
    return;
}

// --- HTTP REQUEST PIPELINE ---
app.UseResponseCompression();
app.Use(async (context, next) =>
{
    context.Items["Spokes.RawRemoteIp"] = context.Connection.RemoteIpAddress;
    await next();
});
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var publicUrl = context.RequestServices.GetService<Spokes_Server.Core.Data.Repositories.Core.SystemConfigRepository>()?.Get()?.ServerPublicUrl;
    if (!string.IsNullOrWhiteSpace(publicUrl) &&
        Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var publicUri) &&
        string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        var rawIp = context.Items.TryGetValue("Spokes.RawRemoteIp", out var ipObj) ? ipObj as System.Net.IPAddress : context.Connection.RemoteIpAddress;
        bool isDirectHttpLoopback = rawIp != null &&
                                    System.Net.IPAddress.IsLoopback(rawIp) &&
                                    !string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString(), Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isDirectHttpLoopback)
        {
            context.Request.Scheme = Uri.UriSchemeHttps;
        }
    }
    await next();
});
app.UseSpokesDiagnostics();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseSpokesResponseHeaders();
app.MapStaticAssets().AllowAnonymous().AllowDuringSetup();
app.UseSpokesSetupRedirect();

app.UseCors();
app.UseSpokesCasdoorSignoutCallbackRedirect();

app.UseAuthentication();
app.UseSpokesSessionRevocation();

// RenderTokenMiddleware MUST run BEFORE UseAuthorization to allow print/PDF rendering token overrides
app.UseMiddleware<RenderTokenMiddleware>();
app.UseSpokesMobileInterceptors();

app.UseAuthorization();
app.UseAntiforgery();
app.UseSpokesMobileDraftAutoReopen();
app.UseSpokesLocalization();
app.UseSpokesFast404Guard();
app.UseSpokesLandingPageRedirect();

// --- ENDPOINTS MAPPING ---
app.MapSpokesEndpoints();

app.Run();

public partial class Program { }
