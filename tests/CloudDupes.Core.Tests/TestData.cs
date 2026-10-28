using System.Runtime.CompilerServices;
using System.Text;
using CloudDupes.Core.Abstractions;
using CloudDupes.Core.Models;

namespace CloudDupes.Core.Tests;

static class TestData
{
    static int _next;

    public static CloudFile File(
        string name,
        long size = 100,
        CloudProviderKind provider = CloudProviderKind.OneDrive,
        string? md5 = null,
        string? sha1 = null,
        string? sha256 = null,
        string? quickXor = null,
        string? path = null,
        DateTimeOffset? modified = null,
        string? id = null) => new()
        {
            Provider = provider,
            Id = id ?? $"id{Interlocked.Increment(ref _next)}",
            Name = name,
            Path = path ?? "/" + name,
            Size = size,
            Modified = modified ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Version = "v1",
            Md5 = md5,
            Sha1 = sha1,
            Sha256 = sha256,
            QuickXor = quickXor,
        };
}

sealed class FakeContentHashes(Dictionary<string, string> hashes) : IContentHashSource
{
    public List<string> Requested { get; } = [];

    public Task<string?> GetSha256Async(CloudFile file, CancellationToken ct)
    {
        lock (Requested) Requested.Add(file.Key);
        return Task.FromResult(hashes.GetValueOrDefault(file.Key));
    }
}

sealed class FakeImageHashes(Dictionary<string, ulong> hashes) : IImageHashSource
{
    public Task<ulong?> GetPerceptualHashAsync(CloudFile file, CancellationToken ct) =>
        Task.FromResult(hashes.TryGetValue(file.Key, out var h) ? h : (ulong?)null);
}

sealed class FakeProvider(CloudProviderKind kind) : ICloudProvider
{
    public Dictionary<string, string> Versions { get; } = [];
    public Dictionary<string, byte[]> Contents { get; } = [];
    public List<string> Trashed { get; } = [];

    public CloudProviderKind Kind => kind;

    public Task<string> SignInAsync(CancellationToken ct) => Task.FromResult("test@example.com");

    public async IAsyncEnumerable<ScanBatch> ScanAsync(string? deltaToken, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }

    public Task<Stream> OpenContentAsync(string id, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Contents[id]));

    public Task<Stream?> OpenThumbnailAsync(string id, CancellationToken ct) => Task.FromResult<Stream?>(null);

    public Task<string?> GetCurrentVersionAsync(string id, CancellationToken ct) => Task.FromResult(Versions.GetValueOrDefault(id));

    public Task TrashAsync(string id, string? expectedVersion, CancellationToken ct)
    {
        Trashed.Add(id);
        Versions.Remove(id);
        return Task.CompletedTask;
    }

    public void SetContent(string id, string text) => Contents[id] = Encoding.UTF8.GetBytes(text);
}
