using CloudDupes.Core.Models;

namespace CloudDupes.Core.Abstractions;

public interface ICloudProvider
{
    CloudProviderKind Kind { get; }

    /// <summary>Signs in (interactively if needed) and returns the account name.</summary>
    Task<string> SignInAsync(CancellationToken ct);

    /// <summary>Full scan when <paramref name="deltaToken"/> is null, otherwise only changes since the token.</summary>
    IAsyncEnumerable<ScanBatch> ScanAsync(string? deltaToken, CancellationToken ct);

    Task<Stream> OpenContentAsync(string id, CancellationToken ct);

    /// <summary>A medium-sized thumbnail, or null when the provider has none.</summary>
    Task<Stream?> OpenThumbnailAsync(string id, CancellationToken ct);

    /// <summary>The current version marker, or null when the item no longer exists or is already trashed.</summary>
    Task<string?> GetCurrentVersionAsync(string id, CancellationToken ct);

    /// <summary>Moves the item to the recycle bin / trash. Never deletes permanently.</summary>
    Task TrashAsync(string id, string? expectedVersion, CancellationToken ct);
}

public interface IContentHashSource
{
    /// <summary>SHA-256 of the file content (lower-case hex), or null if it can't be computed.</summary>
    Task<string?> GetSha256Async(CloudFile file, CancellationToken ct);
}

public interface IImageHashSource
{
    /// <summary>64-bit perceptual hash of the image, or null if unavailable.</summary>
    Task<ulong?> GetPerceptualHashAsync(CloudFile file, CancellationToken ct);
}
