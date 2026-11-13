using Microsoft.Data.Sqlite;

namespace Musebase.Core.Translation;

/// <summary>
/// SQLite 라인 번역 캐시. MT 비용 통제의 핵심 — 같은 (원문, 언어)는 재번역하지 않는다.
/// </summary>
public sealed class SqliteTranslationCache : ITranslationCache, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public SqliteTranslationCache(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS translation_cache (
                text        TEXT NOT NULL,
                target_lang TEXT NOT NULL,
                translation TEXT NOT NULL,
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (text, target_lang)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string? Get(string text, string targetLang)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT translation FROM translation_cache WHERE text = $t AND target_lang = $l";
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$l", targetLang);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void Set(string text, string targetLang, string translation)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO translation_cache (text, target_lang, translation) VALUES ($t, $l, $tr)
                ON CONFLICT (text, target_lang) DO UPDATE SET translation = $tr
                """;
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$l", targetLang);
            cmd.Parameters.AddWithValue("$tr", translation);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>커넥션 풀 해제 — 테스트에서 DB 파일 삭제 전 호출</summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();
}
