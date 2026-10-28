using System.Security.Cryptography;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;
using CloudDupes.Core.Storage;

namespace CloudDupes.Core.Hashing;

/// <summary>
/// OneDrive and Google Drive expose different hash algorithms, so for cross-cloud comparisons
/// the content is streamed once and hashed with SHA-256 locally. Results are cached per version.
/// </summary>
public sealed class CrossCloudHashResolver(
    IReadOnlyDictionary<CloudProviderKind, ICloudProvider> providers,
    InventoryStore store,
    long maxBytes) : IContentHashSource
{
    public int Failures => _failures;
    int _failures;

    public async Task<string?> GetSha256Async(CloudFile file, CancellationToken ct)
    {
        if (file.Sha256 is not null) return file.Sha256;
        if (file.Size > maxBytes) return null;
        if (store.GetContentHash(file) is { } cached) return cached;
        if (!providers.TryGetValue(file.Provider, out var provider)) return null;

        try
        {
            await using var content = await provider.OpenContentAsync(file.Id, ct);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
            store.SaveContentHash(file, hash);
            return hash;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failures);
            return null;
        }
    }
}
