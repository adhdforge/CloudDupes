namespace CloudDupes.Core.Models;

public enum CloudProviderKind
{
    OneDrive,
    GoogleDrive,
}

/// <summary>A file (never a folder) as seen in a cloud drive. Hex hashes are normalized to lower case.</summary>
public sealed record CloudFile
{
    public required CloudProviderKind Provider { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ParentId { get; init; }

    /// <summary>Full path such as "/Photos/2020/img.jpg". Resolved from folders when loaded from the store.</summary>
    public string Path { get; init; } = "";

    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }

    /// <summary>Provider change marker (OneDrive eTag, Google Drive version) used for cache keys and safe deletes.</summary>
    public string? Version { get; init; }

    public string? MimeType { get; init; }
    public string? WebUrl { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public string? QuickXor { get; init; }

    public string Key => $"{Provider}:{Id}";
    public bool IsImage => FileTypes.IsImage(MimeType, Name);
    public string CacheVersion => Version ?? Modified.UtcTicks.ToString();
}

public sealed record FolderEntry(string Id, string? ParentId, string Name);

/// <summary>One page of scan results. Reset means "forget everything stored for this provider first".</summary>
public sealed record ScanBatch(IReadOnlyList<CloudFile> Files, IReadOnlyList<FolderEntry> Folders, IReadOnlyList<string> DeletedIds)
{
    public bool Reset { get; init; }

    /// <summary>Set on the final page only: the token to resume incremental scanning from.</summary>
    public string? NextToken { get; init; }
}

public static class FileTypes
{
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    public static bool IsImage(string? mimeType, string name) =>
        mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        || ImageExtensions.Contains(System.IO.Path.GetExtension(name));
}
