using ImageMagick;

namespace SaveUltimate.Core.Images;

public readonly record struct CropRectangle(int X, int Y, int Width, int Height);

public static class TextureImage
{
    public static byte[] Convert(byte[] dds, uint width, uint height, bool webp, CropRectangle? crop = null)
    {
        if (width == 0 || height == 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (dds.Length < 128 || !dds.AsSpan(0, 4).SequenceEqual("DDS "u8))
            throw new InvalidDataException("Expected a DDS texture.");
        using var image = new MagickImage(dds);
        if (image.Format != MagickFormat.Dds) throw new InvalidDataException("Expected a DDS texture.");
        if (crop is { } rectangle)
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
                (long)rectangle.X + rectangle.Width > image.Width || (long)rectangle.Y + rectangle.Height > image.Height)
                throw new InvalidDataException("Image crop exceeds texture bounds.");
            image.Crop(new MagickGeometry(rectangle.X, rectangle.Y, (uint)rectangle.Width, (uint)rectangle.Height));
            image.ResetPage();
        }
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry(width, height) { Greater = true });
        if (webp)
        {
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
            image.Settings.SetDefine(MagickFormat.WebP, "method", 0);
        }
        return image.ToByteArray(webp ? MagickFormat.WebP : MagickFormat.Png);
    }
}
