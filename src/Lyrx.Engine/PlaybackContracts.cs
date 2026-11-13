namespace Musebase.Engine;

/// <summary>현재 재생 곡 정보(플랫폼 무관 입력 계약).</summary>
public sealed record TrackInfo(string Title, string Artist, string Album, TimeSpan? Duration, string SourceAppId)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}

/// <summary>재생 소스가 지원하는 컨트롤 가용 여부(버튼 활성화 판단용).</summary>
public sealed record PlaybackControls(bool CanPrevious, bool CanPlayPause, bool CanNext)
{
    public static readonly PlaybackControls None = new(false, false, false);
}

/// <summary>
/// "현재 재생 중" 정보 제공자의 플랫폼 무관 계약.
/// Windows=SMTC, Android=MediaSessionManager, macOS=MediaRemote 등이 각각 구현한다.
/// 이벤트는 임의 스레드에서 발생할 수 있으므로 구독자(엔진)가 마샬링을 책임진다.
/// </summary>
public interface INowPlayingSource
{
    /// <summary>현재 곡. 없으면 null.</summary>
    TrackInfo? CurrentTrack { get; }

    /// <summary>재생 중 여부.</summary>
    bool IsPlaying { get; }

    /// <summary>트랙 변경(메타 변화). null = 곡 없음.</summary>
    event Action<TrackInfo?>? TrackChanged;

    /// <summary>재생/일시정지 상태 변경.</summary>
    event Action<bool>? IsPlayingChanged;

    /// <summary>보간된 현재 재생 위치. 취득 불가 시 null.</summary>
    TimeSpan? GetEstimatedPosition();

    /// <summary>현재 세션의 컨트롤 가용 여부.</summary>
    PlaybackControls GetControls();

    /// <summary>재생/일시정지 토글.</summary>
    Task<bool> TogglePlayPauseAsync();

    /// <summary>다음 곡.</summary>
    Task<bool> SkipNextAsync();

    /// <summary>이전 곡.</summary>
    Task<bool> SkipPreviousAsync();
}
