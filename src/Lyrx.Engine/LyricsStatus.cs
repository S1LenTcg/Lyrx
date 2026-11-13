namespace Musebase.Engine;

/// <summary>가사 검색·표시 상태 종류(현지화는 소비자 UI가 담당).</summary>
public enum LyricsStatusKind
{
    NoTrack,
    HiddenByUser,
    Cache,
    Searching,
    Found,
    NotFound,
    Wrong,
    Manual,
    Edited,
}

/// <summary>
/// 엔진이 발행하는 구조화된 상태. 문자열/현지화 대신 종류+데이터를 전달해
/// 엔진을 UI(로컬라이제이션)에서 분리한다. 소비자가 Loc 등으로 문구를 만든다.
/// </summary>
public sealed record LyricsStatus(
    LyricsStatusKind Kind,
    string? Track = null,
    string? Service = null,
    double? Quality = null);

/// <summary>
/// 대상 언어 번역의 표시 상태(가사 소스/품질 옆에 표기). 현지화는 소비자 UI가 담당.
/// UI는 예: "가사: QQMusic (품질 1.04) · 번역: 한도초과"처럼 소스 텍스트 옆에 붙인다.
/// </summary>
public enum TranslationDisplayStatus
{
    None,        // 번역 엔진 꺼짐/불필요(대상=중국어 등) — 표기 안 함
    Translating, // 번역 진행 중
    Live,        // 이번에 번역 API로 채움(정상 번역)
    Cache,       // 캐시에서 채움(오프라인/재재생)
    Quota,       // 한도 초과·요청 제한으로 실패
    Failed,      // 그 외 실패(인증/서버/네트워크)
    Disabled,      // 사용자가 API 번역을 끔 — 채우지 못한 줄이 남아 있음(번역 없이 표시 중)
    DisabledCached, // 사용자가 API 번역을 껐지만 필요한 줄이 캐시/기존 번역으로 모두 채워짐(번역은 정상 표시)
}
