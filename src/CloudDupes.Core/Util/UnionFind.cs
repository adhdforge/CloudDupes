namespace CloudDupes.Core.Util;

public sealed class UnionFind
{
    readonly int[] _parent;

    public UnionFind(int count)
    {
        _parent = new int[count];
        for (var i = 0; i < count; i++) _parent[i] = i;
    }

    public int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]];
            x = _parent[x];
        }
        return x;
    }

    public void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb) _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }
}
