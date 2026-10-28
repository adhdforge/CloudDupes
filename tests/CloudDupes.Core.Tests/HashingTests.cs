using CloudDupes.Core.Hashing;
using CloudDupes.Core.Models;
using CloudDupes.Core.Storage;
using SkiaSharp;

namespace CloudDupes.Core.Tests;

public class HashingTests
{
    static SKBitmap Pattern(int width, int height, int seed)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var random = new Random(seed);
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 12; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawCircle((float)random.NextDouble() * width, (float)random.NextDouble() * height,
                (float)(random.NextDouble() * width / 3 + width / 20.0), paint);
        }
        return bitmap;
    }

    static SKBitmap Reencode(SKBitmap source, int width, int height, int quality)
    {
        using var resized = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(resized);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return SKBitmap.Decode(jpeg);
    }

    [Fact]
    public void Resized_and_recompressed_copy_has_small_distance()
    {
        using var original = Pattern(800, 600, seed: 1);
        using var copy = Reencode(original, 320, 240, quality: 60);
        using var other = Pattern(800, 600, seed: 2);

        var h1 = PerceptualHash.Compute(original);
        var h2 = PerceptualHash.Compute(copy);
        var h3 = PerceptualHash.Compute(other);

        Assert.True(PerceptualHash.Distance(h1, h2) <= 6, $"copy distance {PerceptualHash.Distance(h1, h2)}");
        Assert.True(PerceptualHash.Distance(h1, h3) > 12, $"different image distance {PerceptualHash.Distance(h1, h3)}");
    }

    [Fact]
    public void BkTree_query_matches_brute_force()
    {
        var random = new Random(42);
        var hashes = Enumerable.Range(0, 2000).Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40)).ToArray();
        var tree = new BkTree<int>();
        for (var i = 0; i < hashes.Length; i++) tree.Add(hashes[i], i);

        foreach (var probe in hashes.Take(50))
        {
            var expected = Enumerable.Range(0, hashes.Length).Where(i => PerceptualHash.Distance(probe, hashes[i]) <= 20).ToHashSet();
            Assert.True(expected.SetEquals(tree.Query(probe, 20)));
        }
    }

    [Fact]
    public async Task CrossCloud_resolver_hashes_content_once_and_caches_it()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"clouddupes-test-{Guid.NewGuid():N}.db");
        try
        {
            var store = new InventoryStore(dbPath);
            var provider = new FakeProvider(CloudProviderKind.OneDrive);
            provider.SetContent("f1", "abc");
            var file = TestData.File("a.txt", size: 3, id: "f1");
            var resolver = new CrossCloudHashResolver(new Dictionary<CloudProviderKind, Abstractions.ICloudProvider> { [provider.Kind] = provider }, store, maxBytes: 1000);

            var hash = await resolver.GetSha256Async(file, CancellationToken.None);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);

            provider.Contents.Clear(); // a second download would now throw
            Assert.Equal(hash, await resolver.GetSha256Async(file, CancellationToken.None));
            Assert.Equal(0, resolver.Failures);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task CrossCloud_resolver_respects_size_cap()
    {
        var resolver = new CrossCloudHashResolver(new Dictionary<CloudProviderKind, Abstractions.ICloudProvider>(), null!, maxBytes: 10);
        Assert.Null(await resolver.GetSha256Async(TestData.File("big.bin", size: 11), CancellationToken.None));
    }
}
