using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.DataProtection;
using SkiaSharp;
using Spokes_Server.Aggregate;
using Microsoft.Extensions.Caching.Memory;
namespace Spokes_Server.Controllers;

[Route("spokesapi/files")]
[ApiController]
[Authorize]
public class FileController : SpokesControllerBase
{
    private readonly IFileService _fileService;
    private readonly IEnumerable<IFileAccessProvider> _providers;
    private readonly EmployeeRepository _employees;
    private readonly ScopedKeystoreService _keystore;
    private readonly ICryptoService _crypto;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly ILogger<FileController> _logger;
    private readonly ChatChannelRepository _channels;
    private readonly AlbumRepository _albums;
    private readonly ServerEscrowService _escrowService;
    private readonly UserService _userService;
    private readonly ImageProcessingService _imageService;
    private readonly DeviceSessionRepository _deviceSessions;

    public FileController(
        IFileService fileService,
        IEnumerable<IFileAccessProvider> providers,
        EmployeeRepository employees,
        ChatChannelRepository channels,
        ScopedKeystoreService keystore,
        ICryptoService crypto,
        CompanyProfileRepository companyProfiles,
        OpenIdAccountRepository openIdAccounts,
        ILogger<FileController> logger,
        AlbumRepository albums,
        ServerEscrowService escrowService,
        UserService userService,
        ImageProcessingService imageService,
        DeviceSessionRepository deviceSessions)
    {
        _fileService = fileService;
        _providers = providers;
        _employees = employees;
        _channels = channels;
        _keystore = keystore;
        _crypto = crypto;
        _companyProfiles = companyProfiles;
        _openIdAccounts = openIdAccounts;
        _logger = logger;
        _albums = albums;
        _escrowService = escrowService;
        _userService = userService;
        _imageService = imageService;
        _deviceSessions = deviceSessions;
    }

    // Extensions that should be forced to download instead of inline display (XSS prevention)
    private static readonly HashSet<string> ForceDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".svg", ".xml", ".xhtml"
    };

    private string? TryResolveAlbumKey(string albumId, string userId, string? tokenEncryptionKey = null)
    {
        if (!string.IsNullOrEmpty(tokenEncryptionKey)) return tokenEncryptionKey;

        var album = _albums.GetById(albumId);
        if (album == null || !album.IsEncrypted) return null;

        string? privateKey = null;

        // Step 1: Direct user access (Owner or Contributor)
        if (album.EncryptedAlbumKeys.TryGetValue(userId, out var cipherUserKey))
        {
            if (_keystore.IsUnlocked)
            {
                privateKey = _keystore.GetPrivateKey();
                if (privateKey != null)
                {
                    try { return _crypto.DecryptRsa(cipherUserKey, privateKey); } catch { }
                }
            }
        }

        // Step 2: Channel access
        foreach (var channelId in album.SharedWithChannelIds)
        {
            if (album.EncryptedAlbumKeys.TryGetValue($"Channel_{channelId}", out var cipherAlbumKey))
            {
                var channel = _channels.GetById(channelId);
                if (channel != null && channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherChannelKey))
                {
                    if (_keystore.IsUnlocked)
                    {
                        privateKey ??= _keystore.GetPrivateKey();
                        if (privateKey != null)
                        {
                            try
                            {
                                var channelKey = _crypto.DecryptRsa(cipherChannelKey, privateKey);
                                return _crypto.DecryptAes(cipherAlbumKey, channelKey);
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        // Step 3: Escrow fallback
        if (_escrowService.IsEscrowAvailable && album.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
        {
            try
            {
                return _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
            }
            catch { }
        }

        return null;
    }

    [HttpPost("{category}/{contextId}")]
    public async Task<IActionResult> UploadFile(string category, string contextId, [FromForm] IFormFileCollection files, [FromQuery] string? t = null)
    {
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId))
        {
            return BadRequest("Invalid path parameters");
        }

        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (config.GetValue<bool>("Spokes_DemoMode"))
        {
            var demoUser = _userService.GetEmployee(User);
            if (demoUser == null || !demoUser.IsAdmin)
            {
                return StatusCode(403, "Uploads are disabled in demo mode.");
            }
        }

        if (files == null || files.Count == 0)
        {
            return BadRequest("No files uploaded");
        }

        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return Unauthorized("Authentication required.");
        
        // Extract decryption key from token (auth is handled by [Authorize])
        string? tokenEncryptionKey = null;
        try
        {
            tokenEncryptionKey = ValidateDecryptionToken(category, contextId, "*", t);
        }
        catch (ApplicationException)
        {
            return StatusCode(410, "Upload Token Expired or Invalid");
        }

        var provider = _providers.FirstOrDefault(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (provider != null)
        {
            bool canAccess = await provider.CanAccessAsync(user, contextId);
            if (!canAccess) return Forbid();
        }
        else
        {
            return BadRequest($"Unknown file category: {category}");
        }

        var results = new List<object>();

        long maxFileSize = _companyProfiles.Get()?.MaxFileUploadSizeBytes ?? 268435456;

        string? encryptionKey = null;
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            var channel = _channels.GetById(contextId);
            if (channel != null && channel.IsEncrypted)
            {
                encryptionKey = tokenEncryptionKey;
                
                if (encryptionKey == null)
                {
                    return BadRequest("You do not have the encryption key for this channel (missing token).");
                }
            }
        }
        else if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            var album = _albums.GetById(contextId);
            if (album != null && album.IsEncrypted)
            {
                encryptionKey = TryResolveAlbumKey(contextId, user.Id, tokenEncryptionKey);
                if (encryptionKey == null)
                {
                    return BadRequest("You do not have the encryption key to upload to this album.");
                }
            }
        }

        foreach (var file in files)
        {
            if (file.Length > maxFileSize)
            {
                return BadRequest($"File {file.FileName} exceeds the max size of {maxFileSize} bytes.");
            }

            try
            {
                int? imageWidth = null;
                int? imageHeight = null;
                
                bool hasThumbnail = false;
                
                string safeName = _fileService.GenerateSafeName(file.FileName);
                
                Stream uploadStream = file.OpenReadStream();

                try
                {
                    // Attempt to read image dimensions if it might be an image (skip for temp files to avoid unnecessary processing and orphaned thumbnails)
                    if (!category.Equals("temp", StringComparison.OrdinalIgnoreCase) && file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        // Mitigate memory exhaustion from huge "image bombs"
                        if (file.Length <= 50 * 1024 * 1024) // 50 MB max for image parsing
                        {
                            try
                            {
                                using var skStream = new SKManagedStream(uploadStream, disposeManagedStream: false);
                                using var codec = SKCodec.Create(skStream);
                                if (codec != null)
                                {
                                    imageWidth = codec.Info.Width;
                                    imageHeight = codec.Info.Height;

                                    if (imageWidth > 16384 || imageHeight > 16384 || (long)imageWidth * imageHeight > 100_000_000)
                                    {
                                        throw new InvalidOperationException("Image dimensions are too large, potential decompression bomb.");
                                    }

                                    using var bitmap = SKBitmap.Decode(codec);
                                    if (bitmap != null)
                                    {
                                        var orientedBitmap = bitmap;
                                        bool modifiedMainImage = false;

                                        if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft && codec.EncodedOrigin != SKEncodedOrigin.Default)
                                        {
                                            orientedBitmap = AutoOrientBitmap(bitmap, codec.EncodedOrigin);
                                            modifiedMainImage = true;
                                        }

                                        // Check if main image needs downscaling (max 3840)
                                        int maxMainDim = 3840;
                                        int mainWidth = orientedBitmap.Width;
                                        int mainHeight = orientedBitmap.Height;

                                        if (mainWidth > maxMainDim || mainHeight > maxMainDim)
                                        {
                                            if (mainWidth > mainHeight)
                                            {
                                                mainHeight = (int)Math.Round((double)mainHeight * maxMainDim / mainWidth);
                                                mainWidth = maxMainDim;
                                            }
                                            else
                                            {
                                                mainWidth = (int)Math.Round((double)mainWidth * maxMainDim / mainHeight);
                                                mainHeight = maxMainDim;
                                            }

                                            var resizedBitmap = orientedBitmap.Resize(new SKImageInfo(mainWidth, mainHeight), SKFilterQuality.Medium);
                                            if (orientedBitmap != bitmap) orientedBitmap.Dispose();
                                            orientedBitmap = resizedBitmap;
                                            modifiedMainImage = true;
                                        }

                                        if (modifiedMainImage)
                                        {
                                            var ms = new MemoryStream();
                                            var format = file.ContentType.Contains("png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                                            orientedBitmap.Encode(ms, format, 90);
                                            ms.Position = 0;
                                            
                                            uploadStream.Dispose();
                                            uploadStream = ms;
                                            
                                            imageWidth = orientedBitmap.Width;
                                            imageHeight = orientedBitmap.Height;
                                        }

                                        // GENERATE THUMBNAIL
                                        int maxThumbDim = 1280;

                                        int thumbWidth = orientedBitmap.Width;
                                        int thumbHeight = orientedBitmap.Height;

                                        if (thumbWidth > maxThumbDim || thumbHeight > maxThumbDim)
                                        {
                                            if (thumbWidth > thumbHeight)
                                            {
                                                thumbHeight = (int)Math.Round((double)thumbHeight * maxThumbDim / thumbWidth);
                                                thumbWidth = maxThumbDim;
                                            }
                                            else
                                            {
                                                thumbWidth = (int)Math.Round((double)thumbWidth * maxThumbDim / thumbHeight);
                                                thumbHeight = maxThumbDim;
                                            }
                                        }

                                        // Only resize if needed to save CPU
                                        bool didResize = false;
                                        SKBitmap thumbBitmap = orientedBitmap;
                                        if (thumbWidth != orientedBitmap.Width || thumbHeight != orientedBitmap.Height)
                                        {
                                            thumbBitmap = orientedBitmap.Resize(new SKImageInfo(thumbWidth, thumbHeight), SKFilterQuality.Medium);
                                            didResize = true;
                                        }

                                        if (thumbBitmap != null)
                                        {
                                            var folder = _fileService.GetPhysicalPath(category, contextId, "");
                                            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                                            var thumbPath = Path.Combine(folder, $"{safeName}_thumb.jpg");
                                            
                                            using var thumbStream = new FileStream(thumbPath, FileMode.Create);
                                            using var thumbData = thumbBitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
                                            
                                            if (!string.IsNullOrEmpty(encryptionKey))
                                            {
                                                using var msThumb = new MemoryStream();
                                                thumbData.SaveTo(msThumb);
                                                msThumb.Position = 0;
                                                await _crypto.EncryptStreamAsync(msThumb, thumbStream, encryptionKey);
                                            }
                                            else
                                            {
                                                thumbData.SaveTo(thumbStream);
                                            }
                                            hasThumbnail = true;

                                            if (didResize && thumbBitmap != orientedBitmap)
                                            {
                                                thumbBitmap.Dispose();
                                            }
                                        }

                                        if (orientedBitmap != bitmap)
                                        {
                                            orientedBitmap.Dispose();
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore exceptions if it's not a valid image
                            }
                        }
                        
                        // Reset stream position for the actual upload
                        if (uploadStream.CanSeek)
                        {
                            uploadStream.Position = 0;
                        }
                    }

                    // Pass to IFileService which sanitizes and saves with optional encryption
                    var url = await _fileService.UploadStreamAsync(category, contextId, uploadStream, file.FileName, encryptionKey, safeName);

                    results.Add(new
                    {
                        FilePath = url,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        FileSizeBytes = uploadStream.CanSeek ? uploadStream.Length : file.Length,
                        ImageWidth = imageWidth,
                        ImageHeight = imageHeight,
                        HasThumbnail = hasThumbnail
                    });
                }
                finally
                {
                    uploadStream?.Dispose();
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to process {file.FileName}: {ex.Message}");
            }
        }

        return Ok(results);
    }

    [HttpPost("thumbnail/{category}/{contextId}/{fileName}")]
    public async Task<IActionResult> UploadThumbnail(string category, string contextId, string fileName, [FromForm] IFormFile file, [FromQuery] string? t = null)
    {
        fileName = Path.GetFileName(fileName);
        
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return BadRequest("Invalid path parameters");
        }

        if (category.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Thumbnails are not supported for temporary files.");
        }

        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (config.GetValue<bool>("Spokes_DemoMode"))
        {
            var demoUser = _userService.GetEmployee(User);
            if (demoUser == null || !demoUser.IsAdmin)
            {
                return StatusCode(403, "Uploads are disabled in demo mode.");
            }
        }

        if (file == null || file.Length == 0) return BadRequest("No file uploaded");
        if (file.Length > 5 * 1024 * 1024) return BadRequest("Thumbnail must be smaller than 5MB");

        var user = _userService.GetEmployee(User);
        
        string? tokenEncryptionKey = null;
        try
        {
            tokenEncryptionKey = ValidateDecryptionToken(category, contextId, "*", t);
        }
        catch (ApplicationException)
        {
            return StatusCode(410, "Upload Token Expired or Invalid");
        }

        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return Unauthorized("Authentication required.");

        var provider = _providers.FirstOrDefault(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (provider != null)
        {
            if (!await provider.CanAccessAsync(user, contextId)) return Forbid();

            // Verify ownership to prevent IDOR overwrites
            if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
            {
                var album = _albums.GetById(contextId);
                if (album == null)
                {
                    return Forbid();
                }
            }
        }
        else
        {
            return BadRequest($"Unknown file category: {category}");
        }

        string? encryptionKey = null;
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            var channel = _channels.GetById(contextId);
            if (channel != null && channel.IsEncrypted)
            {
                if (!string.IsNullOrEmpty(tokenEncryptionKey))
                {
                    encryptionKey = tokenEncryptionKey;
                }
                else
                {
                    if (!_keystore.IsUnlocked) return BadRequest("Your chat vault is locked.");
                    var privateKey = _keystore.GetPrivateKey();
                    if (privateKey != null && channel.EncryptedChannelKeys.TryGetValue(user.Id, out var cipherKey))
                    {
                        try { encryptionKey = _crypto.DecryptRsa(cipherKey, privateKey); }
                        catch { return BadRequest("Failed to decrypt channel key."); }
                    }
                    else
                    {
                        return BadRequest("You do not have the encryption key for this channel.");
                    }
                }
            }
        }
        else if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            var album = _albums.GetById(contextId);
            if (album != null && album.IsEncrypted)
            {
                encryptionKey = TryResolveAlbumKey(contextId, user.Id, tokenEncryptionKey);
                if (encryptionKey == null) return BadRequest("No encryption key.");
            }
        }

        var folder = _fileService.GetPhysicalPath(category, contextId, "");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        var fullPath = Path.Combine(folder, $"{fileName}_thumb.jpg");
        if (System.IO.File.Exists(fullPath))
        {
            return BadRequest("Thumbnail already exists for this file. Overwriting is not permitted.");
        }

        using var stream = file.OpenReadStream();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        try
        {
            if (!_imageService.TryGenerateThumbnail(memoryStream, out var thumbData) || thumbData == null)
            {
                return BadRequest("Invalid image format or dimensions too large.");
            }
            
            using (thumbData)
            {
                using var fileStream = new FileStream(fullPath, FileMode.Create);

            if (!string.IsNullOrEmpty(encryptionKey))
            {
                await _crypto.EncryptStreamAsync(thumbData, fileStream, encryptionKey);
            }
            else
            {
                await thumbData.CopyToAsync(fileStream);
            }
            }
        }
        catch
        {
            if (System.IO.File.Exists(fullPath))
            {
                try { System.IO.File.Delete(fullPath); } catch { }
            }
            return BadRequest("Invalid image format.");
        }

        return Ok();
    }

    [HttpGet("thumb/{category}/{contextId}/{fileName}")]
    public Task<IActionResult> GetFileThumb(string category, string contextId, string fileName, [FromQuery] string? t = null)
    {
        return GetFileInternal(category, contextId, fileName, t, thumb: true);
    }

    [HttpGet("{category}/{contextId}/{fileName}")]
    public Task<IActionResult> GetFile(string category, string contextId, string fileName, [FromQuery] string? t = null)
    {
        return GetFileInternal(category, contextId, fileName, t, thumb: false);
    }

    [HttpGet("demo-media/{fileName}")]
    public IActionResult GetDemoMedia(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("Spokes_DemoMode") && !config.GetValue<bool>("Spokes_DemoSetup"))
        {
            return NotFound();
        }

        var dataPath = config["DataPath"] ?? "Data";
        var demoMediaPath = Path.Combine(dataPath, "DemoMedia", fileName);
        
        if (System.IO.File.Exists(demoMediaPath))
        {
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }
            return PhysicalFile(Path.GetFullPath(demoMediaPath), contentType);
        }
        return NotFound();
    }

    [HttpGet("thumb/demo-media/{fileName}")]
    public IActionResult GetDemoMediaThumb(string fileName)
    {
        // Fall back to returning the full size image for demo media thumbnails
        return GetDemoMedia(fileName);
    }

    /// <summary>
    /// Validates a self-contained DataProtection-encrypted token and extracts the AES encryption key.
    /// Does NOT perform authentication — that's handled by [Authorize].
    /// Returns the Base64 encryption key. Throws an exception if the token is provided but invalid.
    /// </summary>
    private string? ValidateDecryptionToken(string category, string contextId, string fileName, string? t)
    {
        if (string.IsNullOrEmpty(t)) return null;

        var fileTokenService = HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        var payload = fileTokenService.UnprotectToken(t);

        if (payload == null)
        {
            throw new ApplicationException("TokenInvalid");
        }

        // Validate token matches this specific file (or wildcard for uploads)
        if (payload.Category != category || payload.ContextId != contextId || (payload.FileName != fileName && fileName != "*" && payload.FileName != "*"))
        {
            throw new ApplicationException("TokenInvalid");
        }

        // Check expiry
        if (DateTime.UtcNow.Ticks > payload.ExpiryTicks)
        {
            throw new ApplicationException("TokenExpired");
        }

        // Return the AES key (may be null for non-encrypted files)
        return payload.EncryptionKeyBase64;
    }

    private async Task<IActionResult> GetFileInternal(string category, string contextId, string fileName, string? t, bool thumb)
    {
        fileName = Path.GetFileName(fileName);
        
        // 0. Validate route parameters - prevent path traversal
        if (!IsValidPathSegment(category) || !IsValidPathSegment(contextId) || !IsValidPathSegment(fileName))
        {
            return BadRequest("Invalid path parameters");
        }

        var extension = Path.GetExtension(fileName);

        // 1. Identify User (always available from [Authorize] middleware)
        var user = _userService.GetEmployee(User);
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return Unauthorized("Authentication required.");

        // Extract decryption key from token (auth is handled by [Authorize])
        string? tokenEncryptionKey = null;
        try
        {
            tokenEncryptionKey = ValidateDecryptionToken(category, contextId, fileName, t);
        }
        catch (ApplicationException)
        {
            return StatusCode(410, "File Token Expired or Invalid");
        }

        // 2. Find Access Provider
        var provider = _providers.FirstOrDefault(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        // If no provider is defined for this category, deny access by default for security
        if (provider == null)
        {
            return BadRequest($"Unknown file category: {category}");
        }

        // Temporary staging files are for internal ingestion only and cannot be downloaded
        if (category.Equals("temp", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        // 3. Check Permissions
        bool canAccess = await provider.CanAccessAsync(user, contextId);
        if (!canAccess)
        {
            return Forbid();
        }

        // 4. Serve File Securely
        var targetFileName = thumb ? $"{fileName}_thumb.jpg" : fileName;
        var path = _fileService.GetPhysicalPath(category, contextId, targetFileName);
        
        if (thumb && !System.IO.File.Exists(path))
        {
            targetFileName = fileName;
            path = _fileService.GetPhysicalPath(category, contextId, targetFileName);
        }

        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var providerType = new FileExtensionContentTypeProvider();
        if (!providerType.TryGetContentType(targetFileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        // Force download for potentially dangerous content types (prevents XSS)
        if (ForceDownloadExtensions.Contains(extension) || contentType.StartsWith("text/html"))
        {
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(fileName)}\"");
        }

        // Add security headers
        Response.Headers.Append("X-Content-Type-Options", "nosniff");
        Response.Headers.Append("Referrer-Policy", "no-referrer");

        // Prevent CDN caching to ensure the [Authorize] attribute is not bypassed by Edge caches
        // But allow the client's browser/WebView to cache it privately (for 2 weeks) to prevent 
        // slow re-downloading when scrolling on mobile devices.
        Response.Headers.Append("Cache-Control", "private, max-age=1209600");

        // 5. Decrypt on the fly if needed
        if (category.Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            var channel = _channels.GetById(contextId);
            if (channel != null && channel.IsEncrypted)
            {
                string? encryptionKey = tokenEncryptionKey;
                
                if (encryptionKey == null)
                {
                    var privateKey = _keystore.GetPrivateKey();
                    if (privateKey != null && channel.EncryptedChannelKeys.TryGetValue(user.Id, out var cipherKey))
                    {
                        try
                        {
                            encryptionKey = _crypto.DecryptRsa(cipherKey, privateKey);
                        }
                        catch
                        {
                            return BadRequest("Failed to decrypt channel key.");
                        }
                    }
                }

                if (encryptionKey != null)
                {
                    try
                    {
                        var keyBytes = Convert.FromBase64String(encryptionKey);

                        var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var seekableStream = new Spokes_Server.Core.Services.Core.SeekableAesStream(fileStream, keyBytes);

                        // Safely stream the fully seekable file, letting ASP.NET Core handle range processing
                        return File(seekableStream, contentType, enableRangeProcessing: true);
                    }
                    catch
                    {
                        return BadRequest("Failed to decrypt channel key.");
                    }
                }
                else
                {
                    return BadRequest("You do not have the encryption key for this channel.");
                }
            }
        }
        else if (category.Equals("albums", StringComparison.OrdinalIgnoreCase))
        {
            var album = _albums.GetById(contextId);
            if (album != null && album.IsEncrypted)
            {
                var encryptionKey = tokenEncryptionKey ?? TryResolveAlbumKey(contextId, user.Id);
                if (encryptionKey != null)
                {
                    var keyBytes = Convert.FromBase64String(encryptionKey);
                    var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var seekableStream = new Spokes_Server.Core.Services.Core.SeekableAesStream(fileStream, keyBytes);
                    return File(seekableStream, contentType, enableRangeProcessing: true);
                }
                else
                {
                    return BadRequest("You do not have the encryption key to view this album.");
                }
            }
        }

        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Validates that a path segment doesn't contain traversal attempts or invalid characters
    /// </summary>
    private static bool IsValidPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        // Block path traversal
        if (segment.Contains("..") || segment.Contains('/') || segment.Contains('\\'))
            return false;

        // Block invalid filename characters
        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return true;
    }

    private static SKBitmap AutoOrientBitmap(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var isRotated = origin == SKEncodedOrigin.LeftTop ||
                        origin == SKEncodedOrigin.RightTop ||
                        origin == SKEncodedOrigin.RightBottom ||
                        origin == SKEncodedOrigin.LeftBottom;

        int newWidth = isRotated ? bitmap.Height : bitmap.Width;
        int newHeight = isRotated ? bitmap.Width : bitmap.Height;

        var oriented = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(oriented);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(bitmap.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(bitmap.Width, bitmap.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, bitmap.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(0, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(bitmap.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(bitmap.Height, bitmap.Width);
                canvas.RotateDegrees(270);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, bitmap.Width);
                canvas.RotateDegrees(270);
                break;
            default:
                return bitmap;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        return oriented;
    }
}



