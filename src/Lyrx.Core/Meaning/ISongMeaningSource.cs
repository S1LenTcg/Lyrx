namespace Musebase.Core.Meaning;

/// <summary>
/// 한 소스에서 가져온 곡 배경 원문. <paramref name="Text"/>는 영어 산문인 경우가 대부분이라
/// 그대로 보여 주지 않고 <see cref="IMeaningWriter"/>가 한국어로 요약한다.
/// </summary>
/// <param name="Name">표시용 소스 이름(출처 표기 의무가 있다 — 화면에 그대로 렌더한다).</param>
/// <param name="Url">사람이 원문을 확인할 주소. 없으면 null.</param>
/// <param name="Text">수집한 본문.</param>
public sealed record MeaningSource(string Name, string? Url, string Text);

/// <summary>
/// 곡 배경 원문을 한 곳에서 가져오는 계약.
///
/// **실패는 예외가 아니라 null이다** — 키 미설정·타임아웃·검색 실패·차단이 모두 같은 결과다
/// (<see cref="Search.HttpRemoteLyricsCache"/>의 조용한 강등과 같은 원칙). 소스 하나가 죽어도
/// 나머지가 채우고, 전부 비면 호출자가 LLM을 아예 부르지 않는다.
/// </summary>
public interface ISongMeaningSource
{
    /// <summary>표시·기록용 소스 id(예: "genius").</summary>
    string Name { get; }

    Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default);
}
