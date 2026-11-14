using System.Text;

namespace Musebase.Android.Services;

/// <summary>
/// 本地文件与 Android Logcat 双通道日志工具。
/// 日志落盘到应用私有目录 <c>FilesDir/lyrx.log</c>，并在内存保留最近 300 条记录供排查。
/// </summary>
internal static class AndroidLog
{
    private const string Tag = "Lyrx";
    private static readonly object Sync = new();
    private static readonly Queue<string> Recent = new();
    private static string? _logPath;

    public static string? ActivePath { get { lock (Sync) return _logPath; } }

    public static void Initialize(string baseDirectory)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(baseDirectory);
            _logPath = Path.Combine(baseDirectory, "lyrx.log");
        }
        Write("=== Lyrx Android 启动 ===");
    }

    public static void Write(string message)
    {
        global::Android.Util.Log.Info(Tag, message);
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Sync)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 300) Recent.Dequeue();

            if (_logPath is null) return;
            try
            {
                using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.WriteLine(line);
            }
            catch
            {
                // 日志写入失败不应影响主流程
            }
        }
    }

    public static IReadOnlyList<string> SnapshotRecent()
    {
        lock (Sync) return Recent.ToArray();
    }
}
