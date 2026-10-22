using System;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Content;

public interface ICompanyBrandingService
{
    bool OptimizeBranding(CompanyProfile profile);
    void SaveWithOptimizedBranding(CompanyProfile profile);
}

/// <summary>
/// Domain service responsible for company branding assets, logo/icon optimization,
/// and image downsampling workflows. Extracted from CompanyProfileRepository to keep
/// repository concerns focused purely on CRUD persistence for company.json.
/// </summary>
public class CompanyBrandingService : ICompanyBrandingService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ImageProcessingService _imageService;
    private readonly ILogger<CompanyBrandingService>? _logger;

    public CompanyBrandingService(
        CompanyProfileRepository companyProfiles,
        ImageProcessingService imageService,
        ILogger<CompanyBrandingService>? logger = null)
    {
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _logger = logger;
    }

    /// <summary>
    /// Optimizes and downsamples oversized branding assets (icon and logo) as part of the configuration update command workflow.
    /// </summary>
    public bool OptimizeBranding(CompanyProfile profile)
    {
        if (profile == null) return false;
        bool changed = false;

        // Auto-downsample oversized legacy icon (> 50KB base64) to max 256px
        if (!string.IsNullOrEmpty(profile.IconBase64) && profile.IconBase64.Length > 50_000)
        {
            if (TryOptimizeBase64(profile.IconBase64, 256, out var optimizedIcon))
            {
                profile.IconBase64 = optimizedIcon;
                profile.IconVersion = Math.Max(1, profile.IconVersion + 1);
                changed = true;
            }
        }

        // Auto-downsample oversized legacy logo (> 500KB base64) to max 1024px
        if (!string.IsNullOrEmpty(profile.LogoBase64) && profile.LogoBase64.Length > 500_000)
        {
            if (TryOptimizeBase64(profile.LogoBase64, 1024, out var optimizedLogo))
            {
                profile.LogoBase64 = optimizedLogo;
                profile.LogoVersion = Math.Max(1, profile.LogoVersion + 1);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Saves the company profile, automatically applying branding optimization as part of the update command workflow.
    /// </summary>
    public void SaveWithOptimizedBranding(CompanyProfile profile)
    {
        OptimizeBranding(profile);
        _companyProfiles.Save(profile);
    }

    private bool TryOptimizeBase64(string dataUri, int maxDimension, out string optimizedUri)
    {
        optimizedUri = dataUri;
        try
        {
            if (!DataUriHelper.TryParse(dataUri, out var rawBytes, out _))
                return false;

            var (optimizedBytes, mimeType) = _imageService.OptimizeImage(rawBytes, maxDimension);
            optimizedUri = $"data:{mimeType};base64," + Convert.ToBase64String(optimizedBytes);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CompanyBrandingService] Failed to optimize image data URI");
            return false;
        }
    }
}
