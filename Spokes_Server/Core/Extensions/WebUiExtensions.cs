using System;
using System.Linq;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Core.Extensions;

public static class WebUiExtensions
{
    public const long DefaultMaxFileUploadSizeBytes = 268435456; // 256MB
    public const long DefaultHubMessageSizeLimitBytes = 64 * 1024 * 1024; // 64MB

    public static IServiceCollection AddSpokesWebUi(
        this IServiceCollection services,
        IWebHostBuilder? webHost = null,
        CompanyProfile? companyProfile = null)
    {
        var maxFileUploadSize = companyProfile?.MaxFileUploadSizeBytes ?? DefaultMaxFileUploadSizeBytes;
        var hubMessageSizeLimit = DefaultHubMessageSizeLimitBytes;

        if (webHost != null)
        {
            webHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = maxFileUploadSize;
            });
        }

        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxFileUploadSize;
        });

        services.ConfigureOptions<KestrelServerOptionsSetup>();
        services.ConfigureOptions<FormOptionsSetup>();
        services.ConfigureOptions<HubOptionsSetup>();
        services.ConfigureOptions<CircuitOptionsSetup>();

        services.AddLocalization();
        services.AddTransient<Microsoft.Extensions.Localization.IStringLocalizer<Spokes_Server.Resources.BusinessResources>, Spokes_Server.Core.Services.UI.CompositeBusinessLocalizer>();

        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(options =>
            {
                options.MaximumReceiveMessageSize = hubMessageSizeLimit;
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.HandshakeTimeout = TimeSpan.FromSeconds(60);
            });

        services.AddResponseCompression(opts =>
        {
            opts.EnableForHttps = true;
            opts.MimeTypes = ResponseCompressionDefaults.MimeTypes;
        });

        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = hubMessageSizeLimit;
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.HandshakeTimeout = TimeSpan.FromSeconds(60);
        });

        services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomLeft;
            config.SnackbarConfiguration.NewestOnTop = true;
            config.SnackbarConfiguration.ShowTransitionDuration = 200;
            config.SnackbarConfiguration.HideTransitionDuration = 200;
        });

        // Ensure SnackbarConfiguration is Scoped per circuit/session so mobile users changing position
        // does not affect desktop or other user sessions
        services.RemoveAll<MudBlazor.SnackbarConfiguration>();
        services.AddScoped<MudBlazor.SnackbarConfiguration>(sp => new MudBlazor.SnackbarConfiguration
        {
            PositionClass = MudBlazor.Defaults.Classes.Position.BottomLeft,
            NewestOnTop = true,
            ShowTransitionDuration = 200,
            HideTransitionDuration = 200
        });

        services.AddControllers(options =>
        {
            options.Filters.Add<GlobalExceptionFilter>();
        });
        services.AddTransient<Spokes_Server.Controllers.FileController>();

        // Presentation Circuit Handlers & User Circuit Context
        services.AddScoped<UserCircuitContext>();
        services.AddScoped<CircuitHandler, PresenceCircuitHandler>();
        services.AddScoped<CircuitHandler, VoiceCircuitHandler>();
        services.AddScoped<CircuitHandler, CultureCircuitHandler>();

        return services;
    }
}

public class KestrelServerOptionsSetup : IConfigureOptions<KestrelServerOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public KestrelServerOptionsSetup(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Configure(KestrelServerOptions options)
    {
        var companyProfiles = _serviceProvider.GetService<CompanyProfileRepository>();
        var maxFileUploadSize = companyProfiles?.Get()?.MaxFileUploadSizeBytes ?? WebUiExtensions.DefaultMaxFileUploadSizeBytes;
        options.Limits.MaxRequestBodySize = maxFileUploadSize;
    }
}

public class FormOptionsSetup : IConfigureOptions<FormOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public FormOptionsSetup(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Configure(FormOptions options)
    {
        var companyProfiles = _serviceProvider.GetService<CompanyProfileRepository>();
        var maxFileUploadSize = companyProfiles?.Get()?.MaxFileUploadSizeBytes ?? WebUiExtensions.DefaultMaxFileUploadSizeBytes;
        options.MultipartBodyLengthLimit = maxFileUploadSize;
    }
}

public class HubOptionsSetup : IConfigureNamedOptions<HubOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public HubOptionsSetup(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Configure(string? name, HubOptions options)
    {
        Configure(options);
    }

    public void Configure(HubOptions options)
    {
        options.MaximumReceiveMessageSize = WebUiExtensions.DefaultHubMessageSizeLimitBytes;

        var systemConfigs = _serviceProvider.GetService<SystemConfigRepository>();
        options.EnableDetailedErrors = systemConfigs?.Get()?.EnableDetailedCircuitErrors ?? false;
    }
}

public class CircuitOptionsSetup : IConfigureOptions<CircuitOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public CircuitOptionsSetup(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Configure(CircuitOptions options)
    {
        var systemConfigs = _serviceProvider.GetService<SystemConfigRepository>();
        options.DetailedErrors = systemConfigs?.Get()?.EnableDetailedCircuitErrors ?? false;
        // Mobile WebViews are suspended for long periods while the page stays alive. A retained
        // circuit lets the client resume with Blazor.reconnect() (a few round trips, UI state kept)
        // instead of a full cold start. MaxRetained bounds memory: the oldest disconnected
        // circuits (e.g. orphaned by an OS-killed app) are evicted first.
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(2);
        options.DisconnectedCircuitMaxRetained = 100;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
    }
}
