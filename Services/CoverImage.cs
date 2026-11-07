using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BagaGame.Services;

/// <summary>
/// Decodes, validates and normalizes poster and banner images. Posters keep a
/// 2:3 format and may use Steam's 2x capsule size, while Big Picture banners
/// are always compressed to the standard 1920x620.
/// </summary>
public static class CoverImage
{
    /// <summary>Smallest stored poster, in 2:3 format.</summary>
    public const int PosterWidth = 600;
    public const int PosterHeight = 900;

    /// <summary>Largest stored poster, matching Steam's 2x capsule.</summary>
    public const int MaxPosterWidth = 1200;
    public const int MaxPosterHeight = 1800;

    /// <summary>Standard wide banner format of the Big Picture hero.</summary>
    public const int HeroWidth = 1920;
    public const int HeroHeight = 620;

    /// <summary>Banners are compressed to the same size as Steam hero art.</summary>
    public const int MaxHeroWidth = 1920;
    public const int MaxHeroHeight = 620;

    private const long MaxFileBytes = 12L * 1024 * 1024;
    private const int MaxDimension = 8000;
    private const long MaxPixels = 40_000_000L;

    public static BitmapSource? LoadFile(string path, int decodeWidth = 0)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 512 || info.Length > MaxFileBytes)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return Decode(stream, decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? LoadBytes(byte[] bytes, int decodeWidth = 0)
    {
        if (bytes.Length < 512 || bytes.Length > MaxFileBytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Decode(stream, decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Center-crops any image to the 2:3 poster format.</summary>
    public static BitmapSource? CenterCrop(BitmapSource source)
    {
        if (!IsSane(source))
        {
            return null;
        }

        var cropHeight = Math.Min(source.PixelHeight, (int)Math.Round(source.PixelWidth * 1.5));
        var cropWidth = Math.Min(source.PixelWidth, (int)Math.Round(cropHeight / 1.5));

        if (cropWidth < 1 || cropHeight < 1)
        {
            return null;
        }

        var rect = new Int32Rect(
            (source.PixelWidth - cropWidth) / 2,
            (source.PixelHeight - cropHeight) / 2,
            cropWidth,
            cropHeight);

        var targetWidth = ClampInt(cropWidth, PosterWidth, MaxPosterWidth);
        var targetHeight = (int)Math.Round(targetWidth * 1.5);
        return Resize(new CroppedBitmap(source, rect), targetWidth, targetHeight);
    }

    /// <summary>Center-crops any image to the wide Big Picture banner format.</summary>
    public static BitmapSource? CenterCropHero(BitmapSource source)
    {
        if (!IsSane(source))
        {
            return null;
        }

        var ratio = (double)HeroWidth / HeroHeight;
        var cropHeight = Math.Min(source.PixelHeight, (int)Math.Round(source.PixelWidth / ratio));
        var cropWidth = Math.Min(source.PixelWidth, (int)Math.Round(cropHeight * ratio));

        if (cropWidth < 1 || cropHeight < 1)
        {
            return null;
        }

        var rect = new Int32Rect(
            (source.PixelWidth - cropWidth) / 2,
            (source.PixelHeight - cropHeight) / 2,
            cropWidth,
            cropHeight);

        var targetWidth = ClampInt(cropWidth, HeroWidth, MaxHeroWidth);
        var targetHeight = (int)Math.Round(targetWidth * (double)HeroHeight / HeroWidth);
        return Resize(new CroppedBitmap(source, rect), targetWidth, targetHeight);
    }

    /// <summary>
    /// Applies the pan/zoom chosen in the crop dialog. Offsets are in viewport
    /// pixels; the result keeps the 2:3 poster format.
    /// </summary>
    public static BitmapSource? CropToPoster(
        BitmapSource source,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportWidth,
        double viewportHeight)
        => CropTo(
            source,
            zoom,
            offsetX,
            offsetY,
            viewportWidth,
            viewportHeight,
            PosterWidth,
            PosterHeight,
            MaxPosterWidth);

    /// <summary>Same pan/zoom crop, but the result keeps the wide banner format.</summary>
    public static BitmapSource? CropToHero(
        BitmapSource source,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportWidth,
        double viewportHeight)
        => CropTo(
            source,
            zoom,
            offsetX,
            offsetY,
            viewportWidth,
            viewportHeight,
            HeroWidth,
            HeroHeight,
            MaxHeroWidth);

    private static BitmapSource? CropTo(
        BitmapSource source,
        double zoom,
        double offsetX,
        double offsetY,
        double viewportWidth,
        double viewportHeight,
        int baseWidth,
        int baseHeight,
        int maxWidth)
    {
        if (!IsSane(source) || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        var baseScale = Math.Max(
            viewportWidth / source.PixelWidth,
            viewportHeight / source.PixelHeight);
        var scale = baseScale * Math.Max(1, zoom);

        var cropWidth = Math.Min(source.PixelWidth, viewportWidth / scale);
        var cropHeight = Math.Min(source.PixelHeight, viewportHeight / scale);

        var left = (source.PixelWidth - cropWidth) / 2 - offsetX / scale;
        var top = (source.PixelHeight - cropHeight) / 2 - offsetY / scale;

        left = Clamp(left, 0, source.PixelWidth - cropWidth);
        top = Clamp(top, 0, source.PixelHeight - cropHeight);

        var x = (int)Math.Round(left);
        var y = (int)Math.Round(top);
        var width = Math.Max(1, (int)Math.Round(cropWidth));
        var height = Math.Max(1, (int)Math.Round(cropHeight));

        width = Math.Min(width, source.PixelWidth - x);
        height = Math.Min(height, source.PixelHeight - y);

        if (width < 1 || height < 1)
        {
            return null;
        }

        var targetWidth = ClampInt(width, baseWidth, maxWidth);
        var targetHeight = (int)Math.Round(targetWidth * (double)baseHeight / baseWidth);

        return Resize(
            new CroppedBitmap(source, new Int32Rect(x, y, width, height)),
            targetWidth,
            targetHeight);
    }

    public static byte[]? ToJpeg(BitmapSource source)
    {
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? Decode(Stream stream, int decodeWidth)
    {
        stream.Position = 0;

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);

        if (decoder.Frames.Count == 0 || !IsSane(decoder.Frames[0]))
        {
            return null;
        }

        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;

        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource Resize(BitmapSource source, int targetWidth, int targetHeight)
    {
        var resized = new TransformedBitmap(
            source,
            new ScaleTransform(
                (double)targetWidth / source.PixelWidth,
                (double)targetHeight / source.PixelHeight));

        resized.Freeze();
        return resized;
    }

    private static bool IsSane(BitmapSource image)
        => image.PixelWidth > 0 &&
           image.PixelHeight > 0 &&
           image.PixelWidth <= MaxDimension &&
           image.PixelHeight <= MaxDimension &&
           (long)image.PixelWidth * image.PixelHeight <= MaxPixels;

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    private static int ClampInt(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}
