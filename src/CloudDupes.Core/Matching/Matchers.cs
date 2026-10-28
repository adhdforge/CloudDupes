using System.Collections.Concurrent;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Hashing;
using CloudDupes.Core.Models;
using CloudDupes.Core.Util;

namespace CloudDupes.Core.Matching;

public interface IDuplicateMatcher
{
    MatchKind Kind { get; }
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<CloudFile> files, CancellationToken ct);
}

/// <summary>
/// Byte-identical files. Uses the hashes the providers already report (free), and for
/// equal-sized files in different clouds falls back to downloading and hashing with SHA-256.
/// </summary>
public sealed class ExactMatcher(IContentHashSource? crossCloud = null, int parallelism = 4, IProgress<string>? progress = null)
    : IDuplicateMatcher
{
    public MatchKind Kind => MatchKind.Exact;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<CloudFile> files, CancellationToken ct)
    {
        var candidates = files.Where(f => f.Size > 0).ToList();
        var uf = new UnionFind(candidates.Count);
        var firstByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);

        void Link(int i, string identity)
        {
            if (firstByIdentity.TryGetValue(identity, out var j)) uf.Union(i, j);
            else firstByIdentity[identity] = i;
        }

        for (var i = 0; i < candidates.Count; i++)
            foreach (var identity in Identities(candidates[i]))
                Link(i, identity);

        if (crossCloud is not null)
        {
            var toHash = CrossCloudCandidates(candidates, uf);
            if (toHash.Count > 0)
            {
                progress?.Report($"Exact: hashing {toHash.Count} file(s) to compare across clouds...");
                var computed = new ConcurrentDictionary<int, string>();
                var done = 0;
                await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                    async (i, token) =>
                    {
                        if (await crossCloud.GetSha256Async(candidates[i], token) is { } hash) computed[i] = hash;
                        var n = Interlocked.Increment(ref done);
                        if (n % 25 == 0 || n == toHash.Count) progress?.Report($"  hashed {n}/{toHash.Count}");
                    });
                foreach (var (i, hash) in computed) Link(i, $"{candidates[i].Size}:sha256:{hash}");
            }
        }

        return Enumerable.Range(0, candidates.Count)
            .GroupBy(uf.Find)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var groupFiles = g.Select(i => candidates[i]).ToList();
                return new DuplicateGroup(MatchKind.Exact, GroupKey(groupFiles[0]), groupFiles);
            })
            .ToList();
    }

    static IEnumerable<string> Identities(CloudFile f)
    {
        // Size is part of every identity as cheap extra protection against hash collisions.
        if (f.QuickXor is { } qx) yield return $"{f.Size}:qx:{qx}";
        if (f.Md5 is { } md5) yield return $"{f.Size}:md5:{md5.ToLowerInvariant()}";
        if (f.Sha1 is { } sha1) yield return $"{f.Size}:sha1:{sha1.ToLowerInvariant()}";
        if (f.Sha256 is { } sha256) yield return $"{f.Size}:sha256:{sha256.ToLowerInvariant()}";
    }

    static string GroupKey(CloudFile f) =>
        f.Sha256 ?? f.Md5 ?? f.Sha1 ?? f.QuickXor ?? $"size:{f.Size}";

    /// <summary>
    /// Files worth downloading. Within one provider the native hashes already decide equality, so two
    /// known-distinct sets can only still be equal if each lives in a single, different provider.
    /// We hash one representative of each such single-provider set, in equal-size buckets only,
    /// skipping representatives that already carry a SHA-256.
    /// </summary>
    static List<int> CrossCloudCandidates(List<CloudFile> files, UnionFind uf)
    {
        var result = new List<int>();
        foreach (var bucket in Enumerable.Range(0, files.Count).GroupBy(i => files[i].Size))
        {
            var singleProviderSets = bucket
                .GroupBy(uf.Find)
                .Where(set => set.Select(i => files[i].Provider).Distinct().Count() == 1)
                .ToList();
            if (singleProviderSets.Select(set => files[set.First()].Provider).Distinct().Count() < 2) continue;

            result.AddRange(singleProviderSets
                .Where(set => set.All(i => files[i].Sha256 is null))
                .Select(set => set.First()));
        }
        return result;
    }
}

/// <summary>Visually similar photos (resized, re-compressed, lightly edited) via perceptual hash.</summary>
public sealed class SimilarImageMatcher(IImageHashSource hashes, int threshold = 8, int parallelism = 8, IProgress<string>? progress = null)
    : IDuplicateMatcher
{
    public MatchKind Kind => MatchKind.Similar;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<CloudFile> files, CancellationToken ct)
    {
        var images = files.Where(f => f.IsImage && f.Size > 0).ToList();
        progress?.Report($"Similar: hashing {images.Count} image thumbnail(s)...");

        var computed = new ConcurrentDictionary<int, ulong>();
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, images.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                if (await hashes.GetPerceptualHashAsync(images[i], token) is { } hash) computed[i] = hash;
                var n = Interlocked.Increment(ref done);
                if (n % 250 == 0 || n == images.Count) progress?.Report($"  thumbnails {n}/{images.Count}");
            });

        var tree = new BkTree<int>();
        foreach (var (i, hash) in computed) tree.Add(hash, i);

        var uf = new UnionFind(images.Count);
        foreach (var (i, hash) in computed)
            foreach (var j in tree.Query(hash, threshold))
                uf.Union(i, j);

        return computed.Keys
            .GroupBy(uf.Find)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var groupFiles = g.OrderBy(i => i).Select(i => images[i]).ToList();
                return new DuplicateGroup(MatchKind.Similar, $"phash:{computed[g.Min()]:x16}", groupFiles);
            })
            .ToList();
    }
}

/// <summary>Loose match on file name (case-insensitive) plus size. Fast, but needs review.</summary>
public sealed class NameSizeMatcher : IDuplicateMatcher
{
    public MatchKind Kind => MatchKind.NameSize;

    public Task<IReadOnlyList<DuplicateGroup>> FindAsync(IReadOnlyList<CloudFile> files, CancellationToken ct)
    {
        IReadOnlyList<DuplicateGroup> groups = files
            .Where(f => f.Size > 0)
            .GroupBy(f => (Name: f.Name.ToLowerInvariant(), f.Size))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(MatchKind.NameSize, $"{g.Key.Name}|{g.Key.Size}", g.ToList()))
            .ToList();
        return Task.FromResult(groups);
    }
}
