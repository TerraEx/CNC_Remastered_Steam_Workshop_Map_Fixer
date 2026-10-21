using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CncRemasterWorkshopFixer;

public sealed record PreviewThumbnailResult(BitmapSource Image, string CacheIdentity);

public sealed class PreviewThumbnailService
{
    public const int ThumbnailSize = 256;

    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public int CachedThumbnailCount
    {
        get { lock (_cacheLock) return _cache.Count; }
    }

    public Task<PreviewThumbnailResult?> LoadAsync(
        WorkshopMapScanResult map,
        string ugcCacheRoot,
        string localMapsRoot,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = MapPreviewSource.TryRead(map, ugcCacheRoot, localMapsRoot);
        if (source is null)
        {
            return null;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(source.CacheIdentity, out var cached))
            {
                return new PreviewThumbnailResult(cached, source.CacheIdentity);
            }
        }

        var decoded = TgaPreviewDecoder.Decode(source.TgaBytes);
        cancellationToken.ThrowIfCancellationRequested();
        // TGA stores straight alpha; Pbgra32 would interpret those bytes as already
        // premultiplied and distort the translucent pixels found in RA previews.
        var fullSize = BitmapSource.Create(
            decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null,
            decoded.BgraPixels, decoded.Stride);
        // Frozen bitmaps can cross from the worker thread to WPF bindings safely.
        fullSize.Freeze();

        BitmapSource thumbnail = fullSize;
        if (decoded.Width > ThumbnailSize || decoded.Height > ThumbnailSize)
        {
            var scale = Math.Min(
                (double)ThumbnailSize / decoded.Width,
                (double)ThumbnailSize / decoded.Height);
            var transformed = new TransformedBitmap(fullSize, new ScaleTransform(scale, scale));
            transformed.Freeze();
            // Materialise the resized pixels so the session cache doesn't retain
            // the original full-size bitmap through TransformedBitmap's source.
            var stride = checked(transformed.PixelWidth * 4);
            var scaledPixels = new byte[checked(stride * transformed.PixelHeight)];
            transformed.CopyPixels(scaledPixels, stride, 0);
            thumbnail = BitmapSource.Create(
                transformed.PixelWidth, transformed.PixelHeight, 96, 96, PixelFormats.Bgra32,
                null, scaledPixels, stride);
            thumbnail.Freeze();
        }

        lock (_cacheLock)
        {
            _cache[source.CacheIdentity] = thumbnail;
            return new PreviewThumbnailResult(thumbnail, source.CacheIdentity);
        }
    }, cancellationToken).ContinueWith(task =>
    {
        if (task.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (task.IsFaulted)
        {
            // Unsupported/corrupt image data never escapes into map lifecycle state.
            return null;
        }
        return task.Result;
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void RemoveEntriesExcept(IReadOnlySet<string> identities)
    {
        lock (_cacheLock)
        {
            foreach (var key in _cache.Keys.Where(key => !identities.Contains(key)).ToList())
            {
                _cache.Remove(key);
            }
        }
    }
}
