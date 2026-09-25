using Microsoft.AspNetCore.Hosting;
using SkiaSharp;

namespace Spokes_Server.Core.Services.Communication;

/// <summary>
/// Service that generates PNG avatars dynamically on the server using SkiaSharp.
/// NOTE: Dynamic server-side image generation is required because external consumers (such as native mobile clients,
/// email clients, and web push notification payloads) request user avatars via static image URLs (e.g. /spokesapi/Media/Avatar/{userId}).
/// Offloading this to client-side CSS/JS/SVG would break avatar display in push notifications, mobile apps, and emails.
/// </summary>
public class AvatarGeneratorService
{
    private readonly IWebHostEnvironment _env;
    private SKTypeface? _typeface;

    public AvatarGeneratorService(IWebHostEnvironment env)
    {
        _env = env;
        LoadFont();
    }

    private void LoadFont()
    {
        try
        {
            string fontPath = Path.Combine(_env.WebRootPath, "fonts", "Roboto-Regular.ttf");
            if (File.Exists(fontPath))
            {
                _typeface = SKTypeface.FromFile(fontPath);
            }
        }
        catch (Exception ex)
        {
            // Fallback will use default typeface if Roboto fails to load
            Console.WriteLine($"[AvatarGeneratorService] Failed to load Roboto font: {ex.Message}");
        }
    }

    public byte[] GenerateAvatar(string? firstName, string? lastName, string? profileColor)
    {
        int size = 192;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);

        // Parse color
        if (string.IsNullOrEmpty(profileColor) || !SKColor.TryParse(profileColor, out SKColor bgColor))
        {
            SKColor.TryParse("#673ab7", out bgColor); // Default MudBlazor DeepPurple
        }

        // Draw background circle
        canvas.Clear(SKColors.Transparent);
        using var bgPaint = new SKPaint
        {
            Color = bgColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawCircle(size / 2f, size / 2f, size / 2f, bgPaint);

        string f = string.IsNullOrEmpty(firstName) ? "" : firstName[..1];
        string l = string.IsNullOrEmpty(lastName) ? "" : lastName[..1];
        string initials = $"{f}{l}".ToUpperInvariant();
        if (string.IsNullOrEmpty(initials))
        {
            initials = "?";
        }

        // Draw text
        using var font = new SKFont
        {
            Typeface = _typeface ?? SKTypeface.Default,
            Size = size * 0.45f
        };

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        font.GetFontMetrics(out SKFontMetrics fontMetrics);
        float textHeight = fontMetrics.Descent - fontMetrics.Ascent;
        float textOffset = (textHeight / 2) - fontMetrics.Descent;

        float textWidth = font.MeasureText(initials);
        float x = (size / 2f) - (textWidth / 2f);
        float y = (size / 2f) + textOffset;

        canvas.DrawText(initials, x, y, font, textPaint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
