using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Musebase.Core.Search;

/// <summary>
/// 按歌曲为单位的歌词缓存。键 = 规范化后的 "标题|艺术家"。
/// 由于整体保存含翻译附件的扩展 LRC 文本，
/// 缓存命中时无需网络（含离线）即可立即显示。
/// </summary>
public sealed class LyricsCacheStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public LyricsCacheStore(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS lyrics_cache (
                key             TEXT PRIMARY KEY,
                lrc             TEXT NOT NULL,
                service         TEXT,
                is_manual       INTEGER NOT NULL DEFAULT 0,
                checked_sources TEXT,
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureExtraColumns();
    }

    private void EnsureExtraColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(lyrics_cache);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        if (!existing.Contains("is_manual"))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE lyrics_cache ADD COLUMN is_manual INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
        if (!existing.Contains("checked_sources"))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE lyrics_cache ADD COLUMN checked_sources TEXT;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>搜索键规范化：转小写 + 空白压缩</summary>
    public static string MakeKey(string title, string artist) =>
        $"{Normalize(title)}|{Normalize(artist)}";

    private static string Normalize(string s) =>
        string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public Lyrics? Get(string title, string artist)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT lrc, service, is_manual, checked_sources, updated_at FROM lyrics_cache WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", MakeKey(title, artist));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var lyrics = Lyrics.Parse(reader.GetString(0));
            if (lyrics is null) return null;
            var service = reader.IsDBNull(1) ? "Cache" : reader.GetString(1);
            lyrics.Metadata.ServiceName = service;
            lyrics.Metadata.IsManualSelection = (!reader.IsDBNull(2) && reader.GetInt64(2) != 0)
                || string.Equals(service, "用户编辑", StringComparison.Ordinal);
            lyrics.Metadata.CheckedSources = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (!reader.IsDBNull(4)
                && DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
            {
                lyrics.Metadata.CachedAtUtc = updated;
            }
            return lyrics;
        }
    }

    public void Set(string title, string artist, Lyrics lyrics)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lyrics_cache (key, lrc, service, is_manual, checked_sources, updated_at)
                VALUES ($k, $l, $s, $m, $c, datetime('now'))
                ON CONFLICT (key) DO UPDATE SET
                    lrc = $l,
                    service = $s,
                    is_manual = $m,
                    checked_sources = $c,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("$k", MakeKey(title, artist));
            cmd.Parameters.AddWithValue("$l", lyrics.ToString());
            cmd.Parameters.AddWithValue("$s", (object?)lyrics.Metadata.ServiceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", lyrics.Metadata.IsManualSelection ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", (object?)lyrics.Metadata.CheckedSources ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            lyrics.Metadata.CachedAtUtc = DateTime.UtcNow;
        }
    }

    public void Remove(string title, string artist)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM lyrics_cache WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", MakeKey(title, artist));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
