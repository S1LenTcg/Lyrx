using Microsoft.Data.Sqlite;

namespace Musebase.Core.Translation;

/// <summary>
/// SQLite 行级翻译缓存。控制机器翻译成本的关键——相同（原文, 语言）不会重复翻译。
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

    /// <summary>释放连接池——测试中删除 DB 文件前调用</summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();
}
