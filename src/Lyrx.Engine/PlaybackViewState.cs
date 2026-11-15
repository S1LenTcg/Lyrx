namespace Musebase.Engine;

/// <summary>逐字卡拉OK锚点（相对行起始的偏移秒数，适用字符索引）。用于序列化。</summary>
public sealed record KaraokeMark(int Index, double Time);

/// <summary>
/// “当前该显示什么”的可序列化显示状态契约（[[0001-core-language]] 第 3 阶段）。
/// 本地 View（WPF/MAUI）绑定该状态，远程显示端（浏览器）通过
/// JSON/WebSocket 接收同一契约并渲染。卡拉OK进度不必逐帧传输，
/// 由 <see cref="LineStartedAt"/> 锚点 + <see cref="Karaoke"/> 让显示端本地插值。
/// 可直接由 System.Text.Json 序列化。
/// </summary>
public sealed record PlaybackViewState(
    bool IsPlaying,
    string? TrackTitle,
    string? TrackArtist,
    string? LineContent,
    string? LineTranslation,
    IReadOnlyList<KaraokeMark>? Karaoke,
    double? KaraokeDurationSeconds,
    DateTimeOffset? LineStartedAt,
    double LineSpanSeconds,
    bool CanPrevious,
    bool CanPlayPause,
    bool CanNext)
{
    public static readonly PlaybackViewState Empty =
        new(false, null, null, null, null, null, null, null, 0, false, false, false);
}
