using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class CompanyProfileRepository : JsonRepository<CompanyProfile>
{
    private bool _hasCheckedOptimization = false;

    public CompanyProfileRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "company.json")
    {
    }

    protected override string GetFilePath(CompanyProfile item)
    {
        return Path.Combine(_basePath, "company.json");
    }

    // Helper to get the ONE profile (creates default if missing)
    public CompanyProfile Get()
    {
        var profile = _cache.Values.FirstOrDefault();
        if (profile == null)
        {
            profile = new CompanyProfile();
            Save(profile);
        }
        else if (!_hasCheckedOptimization)
        {
            EnsureOptimizedBranding(profile);
        }
        return profile;
    }

    private void EnsureOptimizedBranding(CompanyProfile profile)
    {
        _hasCheckedOptimization = true;
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

        if (changed)
        {
            Save(profile);
        }
    }

    private static bool TryOptimizeBase64(string dataUri, int maxDimension, out string optimizedUri)
    {
        optimizedUri = dataUri;
        try
        {
            var commaIdx = dataUri.IndexOf(",");
            if (commaIdx == -1) return false;

            var base64Data = dataUri.Substring(commaIdx + 1);
            var rawBytes = Convert.FromBase64String(base64Data);

            var imageService = new ImageProcessingService();
            var optimizedBytes = imageService.OptimizeImage(rawBytes, maxDimension, out var mimeType);
            optimizedUri = $"data:{mimeType};base64," + Convert.ToBase64String(optimizedBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}


