using CloudDupes.Core.Models;

namespace CloudDupes.Core.Selection;

/// <summary>How to pick the copy to keep in each group.</summary>
/// <param name="Strategy">Used for exact and name+size groups.</param>
/// <param name="SimilarStrategy">Used for similar-photo groups; "largest" keeps the original over resized copies.</param>
/// <param name="PathPriority">Path prefixes in order of preference; applied before the strategy.</param>
public sealed record KeepRules(string Strategy = "oldest", string SimilarStrategy = "largest", IReadOnlyList<string>? PathPriority = null);

public static class KeepSelector
{
    public static readonly string[] Strategies = ["oldest", "newest", "shortest-path", "largest", "prefer:onedrive", "prefer:gdrive"];

    public static bool IsValid(string strategy) => Strategies.Contains(strategy, StringComparer.OrdinalIgnoreCase);

    public static CloudFile Choose(IReadOnlyCollection<CloudFile> files, MatchKind kind, KeepRules rules)
    {
        if (files.Count == 0) throw new ArgumentException("Group is empty.", nameof(files));

        var strategy = (kind == MatchKind.Similar ? rules.SimilarStrategy : rules.Strategy).ToLowerInvariant();
        var ordered = files.OrderBy(f => PriorityIndex(f.Path, rules.PathPriority));
        ordered = strategy switch
        {
            "oldest" => ordered.ThenBy(f => f.Modified),
            "newest" => ordered.ThenByDescending(f => f.Modified),
            "shortest-path" => ordered.ThenBy(f => f.Path.Length),
            "largest" => ordered.ThenByDescending(f => f.Size).ThenBy(f => f.Modified),
            "prefer:onedrive" => ordered.ThenBy(f => f.Provider != CloudProviderKind.OneDrive).ThenBy(f => f.Modified),
            "prefer:gdrive" => ordered.ThenBy(f => f.Provider != CloudProviderKind.GoogleDrive).ThenBy(f => f.Modified),
            _ => throw new ArgumentException($"Unknown keep strategy '{strategy}'. Use one of: {string.Join(", ", Strategies)}."),
        };
        // Deterministic tie-breakers.
        return ordered.ThenBy(f => f.Path.Length).ThenBy(f => f.Key, StringComparer.Ordinal).First();
    }

    static int PriorityIndex(string path, IReadOnlyList<string>? priority)
    {
        if (priority is null) return 0;
        for (var i = 0; i < priority.Count; i++)
            if (path.StartsWith(priority[i], StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }
}
