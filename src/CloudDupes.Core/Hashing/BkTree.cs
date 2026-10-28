namespace CloudDupes.Core.Hashing;

/// <summary>BK-tree over 64-bit hashes with Hamming distance, for fast "within N bits" lookups.</summary>
public sealed class BkTree<T>
{
    sealed class Node(ulong hash, T value)
    {
        public ulong Hash { get; } = hash;
        public List<T> Values { get; } = [value];
        public Dictionary<int, Node> Children { get; } = [];
    }

    Node? _root;

    public void Add(ulong hash, T value)
    {
        if (_root is null)
        {
            _root = new Node(hash, value);
            return;
        }

        var node = _root;
        while (true)
        {
            var d = PerceptualHash.Distance(hash, node.Hash);
            if (d == 0)
            {
                node.Values.Add(value);
                return;
            }
            if (!node.Children.TryGetValue(d, out var child))
            {
                node.Children[d] = new Node(hash, value);
                return;
            }
            node = child;
        }
    }

    public IEnumerable<T> Query(ulong hash, int maxDistance)
    {
        if (_root is null) yield break;

        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var d = PerceptualHash.Distance(hash, node.Hash);
            if (d <= maxDistance)
                foreach (var v in node.Values) yield return v;

            foreach (var (edge, child) in node.Children)
                if (edge >= d - maxDistance && edge <= d + maxDistance)
                    stack.Push(child);
        }
    }
}
