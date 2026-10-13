using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// Decodes each application's cached icon once and shares the result between
/// every window that shows source icons: the same handful of applications run
/// through the whole history, and each window re-asking the database per row
/// would multiply the same work.
/// </summary>
public sealed class AppIconCache
{
    private readonly EntryStore _store;
    private readonly Dictionary<string, ImageSource?> _cache = [];

    public AppIconCache(EntryStore store)
    {
        _store = store;
    }

    /// <summary>The application's icon, Shiyu's-mark territory when there is none.</summary>
    public ImageSource? For(string? sourceApp)
    {
        if (string.IsNullOrEmpty(sourceApp))
        {
            return null;
        }

        if (_cache.TryGetValue(sourceApp, out var cached))
        {
            return cached;
        }

        // Decode at the stored frame's native size (SHGFI_LARGEICON = 32).
        // The icon shows at 18 DIP: 27 physical px at 150%, 22.5 at 125% —
        // every monitor scale renders it by shrinking, never upsampling.
        // The old DecodePixelWidth=16 forced 32→16 at rest and 16→27 at
        // draw time, two lossy passes that read as blur (user report
        // 2026-10-04). One cached copy serves all monitors: the render-side
        // HighQuality scaling resamples per window DPI.
        var icon = Decode(_store.ApplicationIcon(sourceApp), pixelWidth: 32);
        _cache[sourceApp] = icon;
        return icon;
    }

    /// <summary>
    /// Decodes stored PNG bytes at the size they will be shown, because a list
    /// full of rows has no use for the pixels it is not displaying. A width of
    /// zero keeps the stored size: asking for more than a small thumbnail holds
    /// only blows it up twice (once here, once on screen) and blurs it the more.
    /// </summary>
    internal static ImageSource? Decode(byte[]? png, int pixelWidth)
    {
        if (png is null or { Length: 0 })
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (pixelWidth > 0)
            {
                bitmap.DecodePixelWidth = pixelWidth;
            }

            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();

            // Frozen so virtualized lists can recycle rows freely.
            bitmap.Freeze();
            return bitmap;
        }
        catch (NotSupportedException)
        {
            // A stored image that will not decode is not worth a broken window.
            return null;
        }
    }
}
