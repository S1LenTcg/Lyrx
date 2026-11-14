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
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT translation FROM translation_cache WHERE text = $t AND target_lang = $l";
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$l", targetLang);
                return cmd.ExecuteScalar() as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Set(string text, string targetLang, string translation)
    {
        lock (_lock)
        {
            try
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
            catch
            {
                // 缓存写入失败（例如数据库只读/被占用）不应阻断翻译流程
            }
        }
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>释放连接池——测试中删除 DB 文件前调用</summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();
}
