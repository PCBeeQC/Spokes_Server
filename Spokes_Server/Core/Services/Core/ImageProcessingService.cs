using SkiaSharp;
using System;
using System.IO;

namespace Spokes_Server.Core.Services.Core;

public class ImageProcessingService
{
    public bool TryGenerateThumbnail(Stream inputStream, out Stream? thumbnailData)
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

            int maxThumbDim = 1280;
            int thumbWidth = bitmap.Width;
            int thumbHeight = bitmap.Height;

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

            using var thumbBitmap = bitmap.Resize(new SKImageInfo(thumbWidth, thumbHeight), SKFilterQuality.Medium);
            var ms = new MemoryStream();
            (thumbBitmap ?? bitmap).Encode(SKEncodedImageFormat.Jpeg, 80).SaveTo(ms);
            ms.Position = 0;
            thumbnailData = ms;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte[] OptimizeImage(byte[] inputBytes, int maxDimension, out string mimeType)
    {
        try
        {
            using var inputStream = new MemoryStream(inputBytes);
            using var bitmap = SKBitmap.Decode(inputStream);
            if (bitmap == null)
            {
                mimeType = "image/png";
                return inputBytes;
            }

            int width = bitmap.Width;
            int height = bitmap.Height;
            SKBitmap workingBitmap = bitmap;
            bool wasResized = false;

            if (width > maxDimension || height > maxDimension)
            {
                if (width > height)
                {
                    height = Math.Max(1, (int)Math.Round((double)height * maxDimension / width));
                    width = maxDimension;
                }
                else
                {
                    width = Math.Max(1, (int)Math.Round((double)width * maxDimension / height));
                    height = maxDimension;
                }

                var resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
                if (resized != null)
                {
                    workingBitmap = resized;
                    wasResized = true;
                }
            }

            bool isJpeg = inputBytes.Length > 2 && inputBytes[0] == 0xFF && inputBytes[1] == 0xD8;
            SKEncodedImageFormat format = isJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
            int quality = isJpeg ? 85 : 100;
            mimeType = isJpeg ? "image/jpeg" : "image/png";

            using var image = SKImage.FromBitmap(workingBitmap);
            using var data = image.Encode(format, quality);
            var resultBytes = data.ToArray();

            if (wasResized)
            {
                workingBitmap.Dispose();
            }

            return resultBytes;
        }
        catch
        {
            mimeType = "image/png";
            return inputBytes;
        }
    }
}
