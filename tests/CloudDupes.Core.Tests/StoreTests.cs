using CloudDupes.Core.Models;
using CloudDupes.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CloudDupes.Core.Tests;

public sealed class StoreTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"clouddupes-test-{Guid.NewGuid():N}.db");
    readonly InventoryStore _store;

    public StoreTests() => _store = new InventoryStore(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_dbPath + suffix);
    }

    static CloudFile F(string id, string name, string? parent) =>
        TestData.File(name, id: id) with { ParentId = parent, Path = "" };

    [Fact]
    public void Resolves_paths_from_folders_and_follows_renames()
    {
        _store.ApplyBatch(CloudProviderKind.OneDrive, new ScanBatch(
            [F("f1", "a.jpg", "photos"), F("f2", "top.txt", "root")],
            [new FolderEntry("root", null, ""), new FolderEntry("photos", "root", "Photos")],
            []) { Reset = true, NextToken = "t1" });

        var files = _store.LoadFiles().ToDictionary(f => f.Id);
        Assert.Equal("/Photos/a.jpg", files["f1"].Path);
        Assert.Equal("/top.txt", files["f2"].Path);
        Assert.Equal("t1", _store.GetDeltaToken(CloudProviderKind.OneDrive));

        // An incremental batch renames the folder only; the child path must follow.
        _store.ApplyBatch(CloudProviderKind.OneDrive, new ScanBatch([], [new FolderEntry("photos", "root", "Pictures")], []));
        Assert.Equal("/Pictures/a.jpg", _store.LoadFiles().Single(f => f.Id == "f1").Path);
    }

    [Fact]
    public void Unknown_parent_puts_file_at_root()
    {
        _store.ApplyBatch(CloudProviderKind.GoogleDrive, new ScanBatch([F("g1", "x.png", "my-drive-root")], [], []));
        Assert.Equal("/x.png", _store.LoadFiles().Single().Path);
    }

    [Fact]
    public void Deletes_and_reset_only_affect_one_provider()
    {
        _store.ApplyBatch(CloudProviderKind.OneDrive, new ScanBatch([F("a", "a", null), F("b", "b", null)], [], []));
        _store.ApplyBatch(CloudProviderKind.GoogleDrive, new ScanBatch([F("c", "c", null)], [], []));

        _store.ApplyBatch(CloudProviderKind.OneDrive, new ScanBatch([], [], ["a"]));
        Assert.Equal(["b", "c"], _store.LoadFiles().Select(f => f.Id).Order());

        _store.ApplyBatch(CloudProviderKind.GoogleDrive, new ScanBatch([], [], []) { Reset = true });
        Assert.Equal(["b"], _store.LoadFiles().Select(f => f.Id));
        Assert.Equal(["b"], _store.LoadFiles([CloudProviderKind.OneDrive]).Select(f => f.Id));
    }

    [Fact]
    public void Round_trips_all_file_fields()
    {
        var file = TestData.File("a.jpg", size: 1234, md5: "m", sha1: "s1", sha256: "s2", quickXor: "q", id: "x") with
        {
            MimeType = "image/jpeg",
            WebUrl = "https://example.com/a",
            Modified = new DateTimeOffset(2023, 5, 6, 7, 8, 9, TimeSpan.Zero),
        };
        _store.ApplyBatch(CloudProviderKind.OneDrive, new ScanBatch([file], [], []));

        Assert.Equal(file with { Path = "/a.jpg" }, _store.LoadFiles().Single());
    }

    [Fact]
    public void Caches_are_keyed_by_version()
    {
        var file = TestData.File("a.jpg", id: "x");
        _store.SaveContentHash(file, "abc");
        _store.SaveThumbnail(file, new ThumbnailInfo(ulong.MaxValue, [1, 2, 3]));

        Assert.Equal("abc", _store.GetContentHash(file));
        Assert.Equal(ulong.MaxValue, _store.GetThumbnail(file)!.Hash);

        var changed = file with { Version = "v2" };
        Assert.Null(_store.GetContentHash(changed));
        Assert.Null(_store.GetThumbnail(changed));
    }
}
