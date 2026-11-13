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
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS lyrics_cache (
                key        TEXT PRIMARY KEY,
                lrc        TEXT NOT NULL,
                service    TEXT,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
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
            cmd.CommandText = "SELECT lrc, service FROM lyrics_cache WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", MakeKey(title, artist));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var lyrics = Lyrics.Parse(reader.GetString(0));
            if (lyrics is null) return null;
            lyrics.Metadata.ServiceName = reader.IsDBNull(1) ? "Cache" : reader.GetString(1);
            return lyrics;
        }
    }

    public void Set(string title, string artist, Lyrics lyrics)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lyrics_cache (key, lrc, service, updated_at)
                VALUES ($k, $l, $s, datetime('now'))
                ON CONFLICT (key) DO UPDATE SET lrc = $l, service = $s, updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("$k", MakeKey(title, artist));
            cmd.Parameters.AddWithValue("$l", lyrics.ToString());
            cmd.Parameters.AddWithValue("$s", (object?)lyrics.Metadata.ServiceName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
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
