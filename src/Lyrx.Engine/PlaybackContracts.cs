namespace Musebase.Engine;

/// <summary>当前播放歌曲信息(与平台无关的输入契约)。</summary>
public sealed record TrackInfo(string Title, string Artist, string Album, TimeSpan? Duration, string SourceAppId)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}

/// <summary>播放源支持的控件可用性(用于判断按钮是否可用)。</summary>
public sealed record PlaybackControls(bool CanPrevious, bool CanPlayPause, bool CanNext)
{
    public static readonly PlaybackControls None = new(false, false, false);
}

/// <summary>
/// "当前正在播放"信息提供者的与平台无关契约。
/// Windows=SMTC、Android=MediaSessionManager、macOS=MediaRemote 等各自实现。
/// 事件可能在任意线程上触发，因此订阅方(引擎)自己负责线程封送。
/// </summary>
public interface INowPlayingSource
{
    /// <summary>当前歌曲。没有则为 null。</summary>
    TrackInfo? CurrentTrack { get; }

    /// <summary>是否正在播放。</summary>
    bool IsPlaying { get; }

    /// <summary>曲目变更(元数据变化)。null = 没有歌曲。</summary>
    event Action<TrackInfo?>? TrackChanged;

    /// <summary>播放/暂停状态变化。</summary>
    event Action<bool>? IsPlayingChanged;

    /// <summary>插值后的当前播放位置。无法获取时为 null。</summary>
    TimeSpan? GetEstimatedPosition();

    /// <summary>当前会话的控件可用性。</summary>
    PlaybackControls GetControls();

    /// <summary>切换播放/暂停。</summary>
    Task<bool> TogglePlayPauseAsync();

    /// <summary>下一首。</summary>
    Task<bool> SkipNextAsync();

    /// <summary>上一首。</summary>
    Task<bool> SkipPreviousAsync();
}
