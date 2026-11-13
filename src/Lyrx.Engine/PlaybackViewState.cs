namespace Musebase.Engine;

/// <summary>글자 단위 카라오케 앵커(라인 시작 기준 오프셋 초, 적용 문자 인덱스). 직렬화용.</summary>
public sealed record KaraokeMark(int Index, double Time);

/// <summary>
/// "지금 무엇을 보여줄지"의 직렬화 가능한 표시 상태 계약([[0001-core-language]] 3단계).
/// 로컬 View(WPF/MAUI)는 이 상태를 바인딩하고, 원격 디스플레이(브라우저)는 같은 계약을
/// JSON/WebSocket으로 받아 렌더한다. 카라오케 진행은 매 프레임 전송하지 않고
/// <see cref="LineStartedAt"/> 앵커 + <see cref="Karaoke"/>로 표시측이 로컬 보간한다.
/// System.Text.Json으로 그대로 직렬화된다.
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
