using SkiaSharp;

namespace Spokes_Server.Core.Services.Content;

public sealed record ProcessedUploadImage(
    Stream ProcessedStream,
    int? Width,
    int? Height,
    byte[]? ThumbnailBytes,
    byte[]? SmallThumbnailBytes = null);
public sealed record OptimizedImageResult(byte[] Bytes, string MimeType);

public class ImageProcessingService
{
    public const int StandardThumbMaxDim = 1280;
    public const int StandardThumbQuality = 82;
    public const int SmallThumbMaxDim = 400;
    public const int SmallThumbQuality = 82;

    public bool TryGenerateThumbnail(
        Stream inputStream,
        out Stream? thumbnailData,
        int maxDim = StandardThumbMaxDim,
        int quality = StandardThumbQuality)
    {
        thumbnailData = null;
        try
        {
            using var skStream = new SKManagedStream(inputStream, disposeManagedStream: false);
            using var codec = SKCodec.Create(skStream);
            if (codec != null)
            {
                if (codec.Info.Width > 16384 || codec.Info.Height > 16384 || (long)codec.Info.Width * codec.Info.Height > 100_000_000)
                {
                    return false;
                }
            }
            inputStream.Position = 0;

            using var bitmap = SKBitmap.Decode(inputStream);
            if (bitmap == null) return false;

            var orientedBitmap = bitmap;
            if (codec != null && codec.EncodedOrigin != SKEncodedOrigin.TopLeft && codec.EncodedOrigin != SKEncodedOrigin.Default)
            {
                orientedBitmap = AutoOrientBitmap(bitmap, codec.EncodedOrigin);
            }

            try
            {
                var (thumbWidth, thumbHeight) = CalculateFitDimensions(orientedBitmap.Width, orientedBitmap.Height, maxDim);
                using var thumbBitmap = (thumbWidth != orientedBitmap.Width || thumbHeight != orientedBitmap.Height)
                    ? orientedBitmap.Resize(new SKImageInfo(thumbWidth, thumbHeight), new SKSamplingOptions(new SKCubicResampler(1 / 3.0f, 1 / 3.0f)))
                    : null;
                var ms = new MemoryStream();
                using var encoded = (thumbBitmap ?? orientedBitmap).Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
                if (encoded == null)
                {
                    ms.Dispose();
                    return false;
                }
                encoded.SaveTo(ms);
                ms.Position = 0;
                thumbnailData = ms;
                return true;
            }
            finally
            {
                if (!ReferenceEquals(orientedBitmap, bitmap))
                {
                    orientedBitmap.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    public OptimizedImageResult OptimizeImage(byte[] inputBytes, int maxDimension)
    {
        try
        {
            using var inputStream = new MemoryStream(inputBytes);
            using var bitmap = SKBitmap.Decode(inputStream);
            if (bitmap == null)
            {
                return new OptimizedImageResult(inputBytes, "image/png");
            }

            var (width, height) = CalculateFitDimensions(bitmap.Width, bitmap.Height, maxDimension);
            SKBitmap workingBitmap = bitmap;
            bool wasResized = false;

            if (width != bitmap.Width || height != bitmap.Height)
            {
                var resized = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(new SKCubicResampler(1/3.0f, 1/3.0f)));
                if (resized != null)
                {
                    workingBitmap = resized;
                    wasResized = true;
                }
            }

            bool isJpeg = inputBytes is [0xFF, 0xD8, ..];
            SKEncodedImageFormat format = isJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
            int quality = isJpeg ? 85 : 100;
            string mimeType = isJpeg ? "image/jpeg" : "image/png";

            using var image = SKImage.FromBitmap(workingBitmap);
            using var data = image.Encode(format, quality);
            var resultBytes = data.ToArray();

            if (wasResized)
            {
                workingBitmap.Dispose();
            }

            return new OptimizedImageResult(resultBytes, mimeType);
        }
        catch
        {
            return new OptimizedImageResult(inputBytes, "image/png");
        }
    }

    public bool TryProcessUploadImage(
        Stream inputStream,
        string contentType,
        long fileLength,
        out ProcessedUploadImage result,
        int maxMainDim = 3840,
        int quality = 90)
    {
        result = new ProcessedUploadImage(inputStream, null, null, null);

        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Mitigate memory exhaustion from huge "image bombs"
        if (fileLength > 50 * 1024 * 1024)
        {
            return false;
        }

        try
        {
            using var skStream = new SKManagedStream(inputStream, disposeManagedStream: false);
            using var codec = SKCodec.Create(skStream);
            if (codec == null)
            {
                if (inputStream.CanSeek) inputStream.Position = 0;
                return false;
            }

            int imageWidth = codec.Info.Width;
            int imageHeight = codec.Info.Height;

            if (imageWidth > 16384 || imageHeight > 16384 || (long)imageWidth * imageHeight > 100_000_000)
            {
                if (inputStream.CanSeek) inputStream.Position = 0;
                return false;
            }

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null)
            {
                if (inputStream.CanSeek) inputStream.Position = 0;
                return false;
            }

            var orientedBitmap = bitmap;
            bool modifiedMainImage = false;

            if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft && codec.EncodedOrigin != SKEncodedOrigin.Default)
            {
                orientedBitmap = AutoOrientBitmap(bitmap, codec.EncodedOrigin);
                modifiedMainImage = true;
            }

            // Check if main image needs downscaling (maxMainDim > 0; 0 = original/pass-through)
            var (mainWidth, mainHeight) = maxMainDim > 0
                ? CalculateFitDimensions(orientedBitmap.Width, orientedBitmap.Height, maxMainDim)
                : (orientedBitmap.Width, orientedBitmap.Height);

            if (mainWidth != orientedBitmap.Width || mainHeight != orientedBitmap.Height)
            {
                var resizedBitmap = orientedBitmap.Resize(new SKImageInfo(mainWidth, mainHeight), new SKSamplingOptions(SKFilterMode.Linear));
                if (orientedBitmap != bitmap) orientedBitmap.Dispose();
                orientedBitmap = resizedBitmap;
                modifiedMainImage = true;
            }

            Stream processedStream = inputStream;
            if (modifiedMainImage)
            {
                var ms = new MemoryStream();
                var format = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                int encodeQuality = Math.Clamp(quality, 1, 100);
                orientedBitmap.Encode(ms, format, encodeQuality);
                ms.Position = 0;
                processedStream = ms;
                imageWidth = orientedBitmap.Width;
                imageHeight = orientedBitmap.Height;
            }
            else
            {
                imageWidth = orientedBitmap.Width;
                imageHeight = orientedBitmap.Height;
                if (inputStream.CanSeek) inputStream.Position = 0;
            }

            // GENERATE STANDARD (1280px q88) & SMALL GRID (400px q82) THUMBNAILS IN ONE PASS
            var (thumbWidth, thumbHeight) = CalculateFitDimensions(orientedBitmap.Width, orientedBitmap.Height, StandardThumbMaxDim);

            bool didResize = false;
            SKBitmap thumbBitmap = orientedBitmap;
            if (thumbWidth != orientedBitmap.Width || thumbHeight != orientedBitmap.Height)
            {
                var resizedThumb = orientedBitmap.Resize(new SKImageInfo(thumbWidth, thumbHeight), new SKSamplingOptions(new SKCubicResampler(1/3.0f, 1/3.0f)));
                if (resizedThumb != null)
                {
                    thumbBitmap = resizedThumb;
                    didResize = true;
                }
            }

            byte[]? thumbBytes = null;
            byte[]? smallThumbBytes = null;
            if (thumbBitmap != null)
            {
                using var thumbData = thumbBitmap.Encode(SKEncodedImageFormat.Jpeg, StandardThumbQuality);
                thumbBytes = thumbData?.ToArray();

                var (smWidth, smHeight) = CalculateFitDimensions(thumbBitmap.Width, thumbBitmap.Height, SmallThumbMaxDim);
                if (smWidth != thumbBitmap.Width || smHeight != thumbBitmap.Height)
                {
                    using var smallBitmap = thumbBitmap.Resize(new SKImageInfo(smWidth, smHeight), new SKSamplingOptions(new SKCubicResampler(1/3.0f, 1/3.0f)));
                    if (smallBitmap != null)
                    {
                        using var smallData = smallBitmap.Encode(SKEncodedImageFormat.Jpeg, SmallThumbQuality);
                        smallThumbBytes = smallData?.ToArray();
                    }
                }
                else
                {
                    using var smallData = thumbBitmap.Encode(SKEncodedImageFormat.Jpeg, SmallThumbQuality);
                    smallThumbBytes = smallData?.ToArray();
                }

                if (didResize && thumbBitmap != orientedBitmap)
                {
                    thumbBitmap.Dispose();
                }
            }

            if (orientedBitmap != bitmap)
            {
                orientedBitmap.Dispose();
            }

            result = new ProcessedUploadImage(processedStream, imageWidth, imageHeight, thumbBytes, smallThumbBytes);
            return true;
        }
        catch
        {
            if (inputStream.CanSeek) inputStream.Position = 0;
            return false;
        }
    }

    public static SKBitmap AutoOrientBitmap(SKBitmap bitmap, SKEncodedOrigin origin)
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

        canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return oriented;
    }

    private static (int Width, int Height) CalculateFitDimensions(int srcWidth, int srcHeight, int maxDimension)
    {
        if (maxDimension <= 0 || (srcWidth <= maxDimension && srcHeight <= maxDimension))
        {
            return (srcWidth, srcHeight);
        }

        if (srcWidth > srcHeight)
        {
            return (maxDimension, Math.Max(1, (int)Math.Round((double)srcHeight * maxDimension / srcWidth)));
        }

        return (Math.Max(1, (int)Math.Round((double)srcWidth * maxDimension / srcHeight)), maxDimension);
    }
}
