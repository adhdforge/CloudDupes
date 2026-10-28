namespace CloudDupes.Core.Models;

/// <summary>Ordered by trust: earlier kinds win when groups overlap.</summary>
public enum MatchKind
{
    Exact,
    Similar,
    NameSize,
}

public sealed record DuplicateGroup(MatchKind Kind, string Key, IReadOnlyList<CloudFile> Files)
{
    /// <summary>Only exact (content hash) matches are certain; the others should be eyeballed.</summary>
    public bool NeedsReview => Kind != MatchKind.Exact;
}
