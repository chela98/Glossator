using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glossator.App.Helpers;

/// <summary>
/// EPUB kapak görsellerini disk kilidi oluşturmadan (MemoryStream + OnLoad + Freeze) önbellekli olarak yükler.
/// </summary>
public static class BookCoverHelper
{
    private readonly record struct CacheKey(string FullPath, long LastWriteTicksUtc);
    private static readonly ConcurrentDictionary<CacheKey, ImageSource> Cache = new();

    public static ImageSource? LoadCoverBitmap(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!File.Exists(fullPath))
                return null;

            var mtime = File.GetLastWriteTimeUtc(fullPath).Ticks;
            var key = new CacheKey(fullPath, mtime);
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var bytes = File.ReadAllBytes(fullPath);
            using var ms = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 240;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            if (Cache.Count > 48)
            {
                Cache.Clear();
            }

            Cache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
