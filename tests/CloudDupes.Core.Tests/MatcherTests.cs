using CloudDupes.Core.Matching;
using CloudDupes.Core.Models;
using static CloudDupes.Core.Tests.TestData;

namespace CloudDupes.Core.Tests;

public class ExactMatcherTests
{
    const CloudProviderKind OD = CloudProviderKind.OneDrive;
    const CloudProviderKind GD = CloudProviderKind.GoogleDrive;

    [Fact]
    public async Task Groups_files_with_same_native_hash_within_a_provider()
    {
        var a = File("a.jpg", provider: GD, md5: "AAA");
        var b = File("b.jpg", provider: GD, md5: "aaa");
        var c = File("c.jpg", provider: GD, md5: "bbb");
        var d = File("d.jpg", provider: OD, quickXor: "qx1");
        var e = File("e.jpg", provider: OD, quickXor: "qx1");

        var groups = await new ExactMatcher().FindAsync([a, b, c, d, e], CancellationToken.None);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Files.Select(f => f.Key).ToHashSet().SetEquals([a.Key, b.Key]));
        Assert.Contains(groups, g => g.Files.Select(f => f.Key).ToHashSet().SetEquals([d.Key, e.Key]));
    }

    [Fact]
    public async Task Same_hash_but_different_size_is_not_a_duplicate()
    {
        var groups = await new ExactMatcher().FindAsync(
            [File("a", size: 10, md5: "x"), File("b", size: 11, md5: "x")], CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task Empty_files_are_ignored()
    {
        var groups = await new ExactMatcher().FindAsync(
            [File("a", size: 0, md5: "x"), File("b", size: 0, md5: "x")], CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task Shared_sha1_matches_across_clouds_without_downloading()
    {
        var od = File("a.jpg", provider: OD, quickXor: "q", sha1: "ABC");
        var gd = File("a.jpg", provider: GD, md5: "m", sha1: "abc");
        var hashes = new FakeContentHashes([]);

        var groups = await new ExactMatcher(hashes).FindAsync([od, gd], CancellationToken.None);

        Assert.Single(groups);
        Assert.Empty(hashes.Requested);
    }

    [Fact]
    public async Task Cross_cloud_match_downloads_only_equal_size_candidates()
    {
        var od1 = File("a.jpg", size: 500, provider: OD, quickXor: "q1");
        var od2 = File("a-copy.jpg", size: 500, provider: OD, quickXor: "q1");
        var gd = File("a.jpg", size: 500, provider: GD, md5: "m1");
        var lonely = File("b.jpg", size: 999, provider: GD, md5: "m2");
        var hashes = new FakeContentHashes(new() { [od1.Key] = "s", [od2.Key] = "s", [gd.Key] = "s" });

        var groups = await new ExactMatcher(hashes).FindAsync([od1, od2, gd, lonely], CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Files.Count);
        // One representative per known duplicate set, nothing for the unique-size file.
        Assert.Equal(2, hashes.Requested.Count);
        Assert.DoesNotContain(lonely.Key, hashes.Requested);
    }

    [Fact]
    public async Task Cross_cloud_skips_buckets_with_a_single_provider()
    {
        var hashes = new FakeContentHashes([]);
        await new ExactMatcher(hashes).FindAsync(
            [File("a", size: 5, provider: OD, quickXor: "1"), File("b", size: 5, provider: OD, quickXor: "2")], CancellationToken.None);
        Assert.Empty(hashes.Requested);
    }

    [Fact]
    public async Task Cross_cloud_uses_provider_sha256_instead_of_downloading()
    {
        var od = File("a", size: 5, provider: OD, quickXor: "1");
        var gd = File("a", size: 5, provider: GD, md5: "m", sha256: "S");
        var hashes = new FakeContentHashes(new() { [od.Key] = "s" });

        var groups = await new ExactMatcher(hashes).FindAsync([od, gd], CancellationToken.None);

        Assert.Single(groups);
        Assert.Equal([od.Key], hashes.Requested);
    }

    [Fact]
    public async Task Different_content_with_same_size_across_clouds_is_not_grouped()
    {
        var od = File("a", size: 5, provider: OD, quickXor: "1");
        var gd = File("b", size: 5, provider: GD, md5: "m");
        var hashes = new FakeContentHashes(new() { [od.Key] = "s1", [gd.Key] = "s2" });

        Assert.Empty(await new ExactMatcher(hashes).FindAsync([od, gd], CancellationToken.None));
    }
}

public class SimilarAndNameSizeMatcherTests
{
    [Fact]
    public async Task Similar_groups_images_within_threshold_only()
    {
        var a = File("a.jpg");
        var b = File("b.jpg");
        var c = File("c.jpg");
        var doc = File("d.pdf");
        var hashes = new FakeImageHashes(new()
        {
            [a.Key] = 0b0000UL,
            [b.Key] = 0b0111UL,          // distance 3 from a
            [c.Key] = ulong.MaxValue,    // far away
            [doc.Key] = 0,               // not an image, must be ignored
        });

        var groups = await new SimilarImageMatcher(hashes, threshold: 4).FindAsync([a, b, c, doc], CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal([a.Key, b.Key], group.Files.Select(f => f.Key));
        Assert.Equal(MatchKind.Similar, group.Kind);
    }

    [Fact]
    public async Task NameSize_matches_case_insensitive_name_and_equal_size()
    {
        var groups = await new NameSizeMatcher().FindAsync(
            [File("IMG_1.JPG", size: 7), File("img_1.jpg", size: 7), File("img_1.jpg", size: 8)], CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.True(group.NeedsReview);
    }

    [Fact]
    public async Task Finder_drops_groups_already_covered_by_an_earlier_mode()
    {
        var a = File("x.jpg", md5: "h");
        var b = File("x.jpg", md5: "h");
        var c = File("x.jpg", md5: "other");

        var groups = await DuplicateFinder.FindAsync([new NameSizeMatcher(), new ExactMatcher()], [a, b, c], CancellationToken.None);

        // Exact {a,b} runs first; name+size {a,b,c} adds c so it is kept as well.
        Assert.Equal([MatchKind.Exact, MatchKind.NameSize], groups.Select(g => g.Kind));

        var onlyPair = await DuplicateFinder.FindAsync([new NameSizeMatcher(), new ExactMatcher()], [a, b], CancellationToken.None);
        Assert.Equal([MatchKind.Exact], onlyPair.Select(g => g.Kind));
    }
}
