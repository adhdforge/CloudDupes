using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using CloudDupes.Core.Storage;
using SkiaSharp;

namespace CloudDupes.Core.Hashing;

/// <summary>
/// Fetches provider thumbnails, keeps a small JPEG for the report and a perceptual hash for
/// similarity matching. Both are cached in the store keyed by file version.
/// </summary>
public sealed class ThumbnailService(IReadOnlyDictionary<CloudProviderKind, ICloudProvider> providers, InventoryStore store)
    : IImageHashSource
{
    const int ReportSize = 160;

    public int Failures => _failures;
    int _failures;

    public async Task<ulong?> GetPerceptualHashAsync(CloudFile file, CancellationToken ct) =>
        (await GetAsync(file, ct))?.Hash;

    public async Task<ThumbnailInfo?> GetAsync(CloudFile file, CancellationToken ct)
    {
        if (store.GetThumbnail(file) is { } cached) return cached;
        if (!providers.TryGetValue(file.Provider, out var provider)) return null;

        Stream? stream;
        try
        {
            stream = await provider.OpenThumbnailAsync(file.Id, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Network trouble: don't cache, a later run can retry.
            Interlocked.Increment(ref _failures);
            return null;
        }

        var info = new ThumbnailInfo(null, null);
        if (stream is not null)
        {
            await using (stream)
            {
                using var buffer = new MemoryStream();
                try
                {
                    await stream.CopyToAsync(buffer, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _failures);
                    return null;
                }
                // An undecodable thumbnail stays cached as a miss so we don't keep downloading it.
                info = Process(buffer.ToArray()) ?? info;
            }
        }

        store.SaveThumbnail(file, info);
        return info;
    }

    static ThumbnailInfo? Process(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0) return null;

        var scale = Math.Min(1.0, (double)ReportSize / Math.Max(bitmap.Width, bitmap.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
        using var small = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null) return null;

        using var image = SKImage.FromBitmap(small);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 70);
        return new ThumbnailInfo(PerceptualHash.Compute(bitmap), jpeg.ToArray());
    }
}
