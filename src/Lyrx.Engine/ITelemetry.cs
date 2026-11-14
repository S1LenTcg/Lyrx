namespace Musebase.Engine;

/// <summary>
/// 匿名选择性加入的遥测契约(ADR-0004)。事件 schema 的唯一真相是
/// contracts/telemetry-events.md —— type/props 必须遵循该文档。
/// Core/Engine 只通过这个接口发出埋点，排队·发送·同意管理由各平台 head 实现。
/// 实现绝不能抛出异常(采集失败无害)，也不应阻塞调用线程。
/// </summary>
public interface ITelemetry
{
    /// <summary>记录 1 条事件。没有同意时实现会忽略(含 ② 专用类型)。</summary>
    void Track(string type, IReadOnlyDictionary<string, object?>? props = null);
}

/// <summary>同意关闭/未注入时的默认值 —— 采集本身都不做。</summary>
public sealed class NoopTelemetry : ITelemetry
{
    public static readonly NoopTelemetry Instance = new();
    private NoopTelemetry() { }
    public void Track(string type, IReadOnlyDictionary<string, object?>? props = null) { }
}

/// <summary>事件 type 常量(与 contracts/telemetry-events.md 一一对应)。</summary>
public static class TelemetryEvents
{
    // ① 基本统计
    public const string AppSession = "app_session";
    public const string PlaybackSource = "playback_source";
    public const string LyricsSearch = "lyrics_search";
    public const string Translation = "translation";
    public const string FeatureUse = "feature_use";
    public const string Error = "error";
    // ② 质量报告(含歌曲标题/艺术家 —— 需单独同意)
    public const string LyricsNotFound = "lyrics_not_found";
    public const string WrongLyrics = "wrong_lyrics";
}
