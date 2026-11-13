namespace Musebase.Engine;

/// <summary>
/// 익명 옵트인 텔레메트리 계약(ADR-0004). 이벤트 스키마의 단일 진실은
/// contracts/telemetry-events.md — type/props는 반드시 그 문서를 따른다.
/// Core/Engine은 이 인터페이스로 계측만 발화하고, 큐잉·전송·동의 관리는 플랫폼 헤드가 구현한다.
/// 구현은 절대 던지지 않아야 하며(수집 실패는 무해), 호출 스레드를 블로킹하지 않아야 한다.
/// </summary>
public interface ITelemetry
{
    /// <summary>이벤트 1건 기록. 동의가 없으면 구현이 무시한다(② 전용 타입 포함).</summary>
    void Track(string type, IReadOnlyDictionary<string, object?>? props = null);
}

/// <summary>동의 꺼짐/미주입 기본값 — 수집 자체를 하지 않는다.</summary>
public sealed class NoopTelemetry : ITelemetry
{
    public static readonly NoopTelemetry Instance = new();
    private NoopTelemetry() { }
    public void Track(string type, IReadOnlyDictionary<string, object?>? props = null) { }
}

/// <summary>이벤트 type 상수(contracts/telemetry-events.md와 1:1).</summary>
public static class TelemetryEvents
{
    // ① 기본 통계
    public const string AppSession = "app_session";
    public const string PlaybackSource = "playback_source";
    public const string LyricsSearch = "lyrics_search";
    public const string Translation = "translation";
    public const string FeatureUse = "feature_use";
    public const string Error = "error";
    // ② 품질 리포트(곡 제목/아티스트 포함 — 별도 동의)
    public const string LyricsNotFound = "lyrics_not_found";
    public const string WrongLyrics = "wrong_lyrics";
}
