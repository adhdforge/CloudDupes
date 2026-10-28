using System.Globalization;
using CloudDupes.Core.Models;
using Microsoft.Data.Sqlite;

namespace CloudDupes.Core.Storage;

public sealed record ThumbnailInfo(ulong? Hash, byte[]? Jpeg);

public sealed record ProviderStats(CloudProviderKind Provider, int Files, long Bytes, int Folders);

/// <summary>Local SQLite cache of the cloud inventory, delta tokens and computed hashes.</summary>
public sealed class InventoryStore
{
    readonly string _connectionString;
    readonly Lock _writeLock = new();

    public InventoryStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = 60,
        }.ToString();
        Initialize();
    }

    void Initialize()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS files(
                provider TEXT NOT NULL, id TEXT NOT NULL, parent_id TEXT, name TEXT NOT NULL,
                size INTEGER NOT NULL, modified TEXT NOT NULL, version TEXT, mime TEXT, web_url TEXT,
                md5 TEXT, sha1 TEXT, sha256 TEXT, quickxor TEXT,
                PRIMARY KEY(provider, id));
            CREATE TABLE IF NOT EXISTS folders(
                provider TEXT NOT NULL, id TEXT NOT NULL, parent_id TEXT, name TEXT NOT NULL,
                PRIMARY KEY(provider, id));
            CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS content_hashes(
                provider TEXT NOT NULL, id TEXT NOT NULL, version TEXT NOT NULL, sha256 TEXT NOT NULL,
                PRIMARY KEY(provider, id));
            CREATE TABLE IF NOT EXISTS thumbnails(
                provider TEXT NOT NULL, id TEXT NOT NULL, version TEXT NOT NULL, phash INTEGER, jpeg BLOB,
                PRIMARY KEY(provider, id));
            """);
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ---- inventory -------------------------------------------------------------------------

    public void ApplyBatch(CloudProviderKind provider, ScanBatch batch)
    {
        var p = provider.ToString();
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            if (batch.Reset)
            {
                using var clear = Command(c, "DELETE FROM files WHERE provider=$p; DELETE FROM folders WHERE provider=$p;", ("$p", p));
                clear.ExecuteNonQuery();
            }

            using var delete = Command(c, "DELETE FROM files WHERE provider=$p AND id=$id; DELETE FROM folders WHERE provider=$p AND id=$id;",
                ("$p", p), ("$id", ""));
            foreach (var id in batch.DeletedIds)
            {
                delete.Parameters["$id"].Value = id;
                delete.ExecuteNonQuery();
            }

            // An id can switch between file and folder only in theory, but clear the other table anyway.
            using var upsertFolder = Command(c, """
                DELETE FROM files WHERE provider=$p AND id=$id;
                INSERT OR REPLACE INTO folders(provider, id, parent_id, name) VALUES($p, $id, $parent, $name);
                """, ("$p", p), ("$id", ""), ("$parent", null), ("$name", ""));
            foreach (var f in batch.Folders)
            {
                upsertFolder.Parameters["$id"].Value = f.Id;
                upsertFolder.Parameters["$parent"].Value = (object?)f.ParentId ?? DBNull.Value;
                upsertFolder.Parameters["$name"].Value = f.Name;
                upsertFolder.ExecuteNonQuery();
            }

            using var upsertFile = Command(c, """
                DELETE FROM folders WHERE provider=$p AND id=$id;
                INSERT OR REPLACE INTO files(provider, id, parent_id, name, size, modified, version, mime, web_url, md5, sha1, sha256, quickxor)
                VALUES($p, $id, $parent, $name, $size, $modified, $version, $mime, $web, $md5, $sha1, $sha256, $qx);
                """,
                ("$p", p), ("$id", ""), ("$parent", null), ("$name", ""), ("$size", 0L), ("$modified", ""), ("$version", null),
                ("$mime", null), ("$web", null), ("$md5", null), ("$sha1", null), ("$sha256", null), ("$qx", null));
            foreach (var f in batch.Files)
            {
                upsertFile.Parameters["$id"].Value = f.Id;
                upsertFile.Parameters["$parent"].Value = (object?)f.ParentId ?? DBNull.Value;
                upsertFile.Parameters["$name"].Value = f.Name;
                upsertFile.Parameters["$size"].Value = f.Size;
                upsertFile.Parameters["$modified"].Value = f.Modified.ToString("O", CultureInfo.InvariantCulture);
                upsertFile.Parameters["$version"].Value = (object?)f.Version ?? DBNull.Value;
                upsertFile.Parameters["$mime"].Value = (object?)f.MimeType ?? DBNull.Value;
                upsertFile.Parameters["$web"].Value = (object?)f.WebUrl ?? DBNull.Value;
                upsertFile.Parameters["$md5"].Value = (object?)f.Md5 ?? DBNull.Value;
                upsertFile.Parameters["$sha1"].Value = (object?)f.Sha1 ?? DBNull.Value;
                upsertFile.Parameters["$sha256"].Value = (object?)f.Sha256 ?? DBNull.Value;
                upsertFile.Parameters["$qx"].Value = (object?)f.QuickXor ?? DBNull.Value;
                upsertFile.ExecuteNonQuery();
            }

            if (batch.NextToken is not null)
            {
                using var token = Command(c, "INSERT OR REPLACE INTO state(key, value) VALUES($k, $v)",
                    ("$k", DeltaKey(provider)), ("$v", batch.NextToken));
                token.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public string? GetDeltaToken(CloudProviderKind provider) => GetState(DeltaKey(provider));

    static string DeltaKey(CloudProviderKind provider) => $"delta:{provider}";

    public string? GetState(string key)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT value FROM state WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void RemoveFile(CloudProviderKind provider, string id)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = Command(c, "DELETE FROM files WHERE provider=$p AND id=$id", ("$p", provider.ToString()), ("$id", id));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Loads all files (optionally for some providers only) with their full paths resolved.</summary>
    public List<CloudFile> LoadFiles(IReadOnlyCollection<CloudProviderKind>? providers = null)
    {
        using var c = Open();
        var folders = new Dictionary<(string, string), FolderEntry>();
        using (var cmd = Command(c, "SELECT provider, id, parent_id, name FROM folders"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                folders[(r.GetString(0), r.GetString(1))] = new FolderEntry(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
        }

        var folderPaths = new Dictionary<(string, string), string>();
        string FolderPath(string provider, string? folderId, int depth = 0)
        {
            if (folderId is null || depth > 256 || !folders.TryGetValue((provider, folderId), out var folder)) return "";
            if (folderPaths.TryGetValue((provider, folderId), out var cached)) return cached;
            var parentPath = FolderPath(provider, folder.ParentId, depth + 1);
            var path = folder.Name.Length == 0 ? parentPath : $"{parentPath}/{folder.Name}";
            folderPaths[(provider, folderId)] = path;
            return path;
        }

        var result = new List<CloudFile>();
        using (var cmd = Command(c, "SELECT provider, id, parent_id, name, size, modified, version, mime, web_url, md5, sha1, sha256, quickxor FROM files"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var provider = Enum.Parse<CloudProviderKind>(r.GetString(0));
                if (providers is not null && !providers.Contains(provider)) continue;
                var parentId = r.IsDBNull(2) ? null : r.GetString(2);
                var name = r.GetString(3);
                result.Add(new CloudFile
                {
                    Provider = provider,
                    Id = r.GetString(1),
                    ParentId = parentId,
                    Name = name,
                    Path = $"{FolderPath(r.GetString(0), parentId)}/{name}",
                    Size = r.GetInt64(4),
                    Modified = DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Version = NullableString(r, 6),
                    MimeType = NullableString(r, 7),
                    WebUrl = NullableString(r, 8),
                    Md5 = NullableString(r, 9),
                    Sha1 = NullableString(r, 10),
                    Sha256 = NullableString(r, 11),
                    QuickXor = NullableString(r, 12),
                });
            }
        }
        return result;
    }

    static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public List<ProviderStats> GetStats()
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT p.provider,
                   (SELECT COUNT(*) FROM files f WHERE f.provider = p.provider),
                   (SELECT COALESCE(SUM(size), 0) FROM files f WHERE f.provider = p.provider),
                   (SELECT COUNT(*) FROM folders d WHERE d.provider = p.provider)
            FROM (SELECT provider FROM files UNION SELECT provider FROM folders) p
            """);
        using var r = cmd.ExecuteReader();
        var stats = new List<ProviderStats>();
        while (r.Read())
            stats.Add(new ProviderStats(Enum.Parse<CloudProviderKind>(r.GetString(0)), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3)));
        return stats;
    }

    // ---- hash caches -----------------------------------------------------------------------

    public string? GetContentHash(CloudFile file)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT sha256 FROM content_hashes WHERE provider=$p AND id=$id AND version=$v",
            ("$p", file.Provider.ToString()), ("$id", file.Id), ("$v", file.CacheVersion));
        return cmd.ExecuteScalar() as string;
    }

    public void SaveContentHash(CloudFile file, string sha256)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = Command(c, "INSERT OR REPLACE INTO content_hashes(provider, id, version, sha256) VALUES($p, $id, $v, $h)",
                ("$p", file.Provider.ToString()), ("$id", file.Id), ("$v", file.CacheVersion), ("$h", sha256));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Null when not cached. A cached entry with a null hash means "no usable thumbnail".</summary>
    public ThumbnailInfo? GetThumbnail(CloudFile file)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT phash, jpeg FROM thumbnails WHERE provider=$p AND id=$id AND version=$v",
            ("$p", file.Provider.ToString()), ("$id", file.Id), ("$v", file.CacheVersion));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ThumbnailInfo(
            r.IsDBNull(0) ? null : unchecked((ulong)r.GetInt64(0)),
            r.IsDBNull(1) ? null : (byte[])r.GetValue(1));
    }

    public void SaveThumbnail(CloudFile file, ThumbnailInfo info)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = Command(c, "INSERT OR REPLACE INTO thumbnails(provider, id, version, phash, jpeg) VALUES($p, $id, $v, $h, $j)",
                ("$p", file.Provider.ToString()), ("$id", file.Id), ("$v", file.CacheVersion),
                ("$h", info.Hash is { } h ? unchecked((long)h) : null), ("$j", info.Jpeg));
            cmd.ExecuteNonQuery();
        }
    }
}
