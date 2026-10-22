using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace SaveUltimate.App;

public static class ItemImages
{
    private static readonly Dictionary<(string, uint, bool), BitmapImage?> cache = new();
    internal static void Clear() => cache.Clear();
    // Items without a game image show nothing; the Theme style collapses an Image whose Source is null.
    public static BitmapImage? Get(uint key, uint size) =>
        GetFile(Path.Combine(AppContext.BaseDirectory, "icons_local", $"{key}.webp"), size)
        ?? GetFile(Path.Combine(AppContext.BaseDirectory, "game_data", "images", "items", $"{key}.webp"), size);

    public static BitmapImage? GetFile(string path, uint size, bool trim = false)
    {
        // Rows re-request icons on every scroll; answer cached paths without touching the disk.
        if (cache.TryGetValue((path, size, trim), out var cached)) return cached;
        if (!File.Exists(path)) return null;
        BitmapImage? bitmap = null;
        try
        {
            using var image = new MagickImage(path, new MagickReadSettings { BackgroundColor = MagickColors.Transparent });
            if (trim) { image.Trim(); image.ResetPage(); }
            image.Thumbnail(size, size);
            using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
            bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        }
        catch (Exception error) when (error is MagickException or IOException or UnauthorizedAccessException) { }
        if (cache.Count >= 256) cache.Clear();
        cache[(path, size, trim)] = bitmap;
        return bitmap;
    }
}
