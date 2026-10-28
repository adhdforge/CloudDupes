using CloudDupes.Core.Models;

namespace CloudDupes.Core.Matching;

public static class DuplicateFinder
{
    /// <summary>
    /// Runs the matchers in trust order (exact, similar, name+size) and drops any group whose files
    /// are already all inside one earlier group, so the same duplicates aren't reported twice.
    /// </summary>
    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        IEnumerable<IDuplicateMatcher> matchers, IReadOnlyList<CloudFile> files, CancellationToken ct)
    {
        var result = new List<DuplicateGroup>();
        var keySets = new List<HashSet<string>>();
        var groupsByFile = new Dictionary<string, List<int>>();

        foreach (var matcher in matchers.OrderBy(m => m.Kind))
        {
            foreach (var group in await matcher.FindAsync(files, ct))
            {
                var keys = group.Files.Select(f => f.Key).ToHashSet();
                if (groupsByFile.TryGetValue(group.Files[0].Key, out var existing)
                    && existing.Any(index => keys.IsSubsetOf(keySets[index])))
                    continue;

                var index = result.Count;
                result.Add(group);
                keySets.Add(keys);
                foreach (var key in keys)
                {
                    if (!groupsByFile.TryGetValue(key, out var list)) groupsByFile[key] = list = [];
                    list.Add(index);
                }
            }
        }
        return result;
    }
}
