using Microsoft.Data.Sqlite;

namespace Musebase.Core.Search;

/// <summary>
/// 곡 단위 가사 캐시. 키 = 정규화된 "제목|아티스트".
/// 번역 첨부까지 포함한 확장 LRC 텍스트를 통째로 저장하므로
/// 캐시 히트 시 네트워크 없이(오프라인 포함) 즉시 표시된다.
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

    /// <summary>검색 키 정규화: 소문자 + 공백 축약</summary>
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
