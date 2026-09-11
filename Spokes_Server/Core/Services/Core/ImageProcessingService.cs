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
}
