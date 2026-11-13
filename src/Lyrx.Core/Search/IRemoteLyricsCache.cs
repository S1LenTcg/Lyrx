namespace Musebase.Core.Search;

/// <summary>
/// 원격 조회 1회의 결과. 미스여도 "다른 기기가 지금 같은 곡을 찾고 있다"는 힌트가 붙을 수 있어
/// 단순 <c>Lyrics?</c>로는 부족하다(`contracts/lyrics-api.md`의 "번역 양보").
/// </summary>
/// <param name="Lyrics">받은 가사. 미스·실패면 null.</param>
/// <param name="Pending">최근에 다른 기기도 이 곡을 물었다 — 번역을 잠시 양보할 만하다.</param>
/// <param name="RetryAfterMs">서버가 제안하는 재조회 간격(0이면 제안 없음). 호출자가 clamp한다.</param>
/// <param name="Langs">받은 가사에 들어 있는 번역 언어들(소문자). 히트가 아니면 빈 배열.</param>
/// <param name="IsAd">
/// 서버가 이 제목을 <b>광고로 표시</b>해 뒀다. 제공자 검색을 하지 말아야 한다 — 광고는 곡이
/// 아니라서 검색이 늘 헛돌고, 어쩌다 뭔가 맞으면 엉뚱한 가사가 광고 위에 뜬다.
/// </summary>
public readonly record struct RemoteLyricsResult(
    Lyrics? Lyrics, bool Pending, int RetryAfterMs, IReadOnlyList<string> Langs, bool IsAd = false)
{
    /// <summary>미스·실패·미접속 — 아무 힌트도 없다.</summary>
    public static readonly RemoteLyricsResult Miss = new(null, false, 0, []);

    /// <summary>서버가 광고로 표시한 제목 — 검색하지 않는다.</summary>
    public static readonly RemoteLyricsResult Ad = new(null, false, 0, [], IsAd: true);

    /// <summary>대상 언어 번역이 들어 있는가(대소문자 무시).</summary>
    public bool HasLanguage(string lang) =>
        Langs.Contains(lang, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 원격 가사 캐시(개인 서버)의 계약. 로컬 캐시와 제공자 검색 사이에 끼어드는 **가산 계층**이다 —
/// 서버가 없거나 못 붙으면 조용히 기존 동작(제공자 검색)으로 강등되어야 하므로,
/// 구현은 **예외를 밖으로 던지지 않는다**(실패·타임아웃·미접속 = <see cref="RemoteLyricsResult.Miss"/> / 저장 무시).
///
/// HTTP 계약은 `contracts/lyrics-api.md`(v1), 참조 구현은 <see cref="HttpRemoteLyricsCache"/>.
/// </summary>
public interface IRemoteLyricsCache
{
    /// <summary>서버에서 가사를 가져온다. 미스·실패·타임아웃은 모두 <see cref="RemoteLyricsResult.Miss"/>.</summary>
    Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>서버에 가사를 올린다(업서트). 실패는 조용히 무시하므로 호출자가 await하지 않아도 된다.</summary>
    Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default);

    /// <summary>
    /// 곡의 의미를 가져온다. 없으면 null — 이 호출은 <b>만들지 않는다</b>.
    /// 가사와 마찬가지로 실패·미접속도 조용히 null이다.
    /// </summary>
    Task<SongMeaningView?> GetMeaningAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>
    /// 의미 조회 + "서버가 지금 만드는 중" 힌트(404 본문의 <c>pending</c>). 새로 올라온 곡은 서버가
    /// 자동으로 만들므로, 힌트가 오면 잠시 뒤 다시 묻는다(<see cref="MeaningFetch"/>).
    /// 기본 구현은 힌트를 모른다 — 구현이 없는 테스트 대역·구버전과 그대로 호환된다.
    /// </summary>
    async Task<MeaningLookup> LookupMeaningAsync(string title, string artist, CancellationToken ct = default) =>
        new(await GetMeaningAsync(title, artist, ct).ConfigureAwait(false), false, 0);

    /// <summary>
    /// 곡의 의미를 <b>지금 만들어 달라고</b> 서버에 요청한다. 사람이 버튼을 눌렀을 때만 부른다 —
    /// 한 번이 외부 API 여러 개 + LLM 호출이라 비싸다(자동 호출 금지).
    ///
    /// 조회와 달리 수십 초가 걸릴 수 있어 <b>훨씬 긴 타임아웃</b>을 쓰고, 실패해도
    /// 서킷 브레이커에 세지 않는다(부가 기능 때문에 가사 조회가 막히면 손해가 크다).
    /// </summary>
    /// <param name="force">
    /// <b>이미 있는 의미를 버리고 다시 만든다.</b> 기본값(false)이면 서버가 만들어 둔 것이 있을 때
    /// 그것을 그대로 돌려준다 — 비싼 호출을 되풀이하지 않고, 무엇보다 <b>기존 글을 덮어쓰지 않는다</b>.
    /// 사람이 "다시 만들기"를 눌러 확인했을 때만 true를 준다.
    /// </param>
    Task<MeaningRequestResult> RequestMeaningAsync(
        string title, string artist, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// 곡에 딸린 것들(커버 주소·좋아요 상태)을 한 번에 받는다.
    ///
    /// 못 받았을 때 <b>이유를 함께 준다</b>(<see cref="ExtrasReach"/>) — 처음 트는 곡은 아직
    /// 아무도 올리지 않아 404가 정상인데, 그것을 연결 오류로 그리면 새 곡마다 경고가 뜬다.
    ///
    /// 서버는 아직 커버를 안 찾아본 곡이면 <b>이 호출에서 찾는다</b>(한 곡당 한 번). 그래서
    /// 가사 조회보다 느릴 수 있으니 화면을 막지 말고 받는 대로 채워 넣어야 한다.
    /// </summary>
    Task<SongExtrasResult> GetExtrasAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>커버를 처음부터 다시 찾게 한다(곡명을 고친 뒤). 사람이 눌렀을 때만 부른다.</summary>
    Task<SongExtras?> RefreshCoverAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>Last.fm 좋아요를 켜거나 끈다. 실패하면 null — 화면을 바꾸지 말아야 한다.</summary>
    Task<SongExtras?> SetLovedAsync(string title, string artist, bool loved, CancellationToken ct = default);
}

/// <summary>
/// 서버에 물어본 결과. <b>"서버가 그 곡을 모른다"와 "서버에 못 물어봤다"는 다르다</b> —
/// 둘 다 값이 비지만 사람에게 할 말이 정반대다.
///
/// 처음 트는 곡은 아직 아무도 올리지 않아 <see cref="NotFound"/>가 정상이다(몇 초 뒤 이 기기가
/// 올린다). 이걸 오류로 그리면 <b>멀쩡한 새 곡마다 경고가 뜬다</b>.
/// </summary>
public enum ExtrasReach
{
    /// <summary>받아 왔다.</summary>
    Ok,

    /// <summary>서버는 답했지만 그 곡을 모른다(404). 오류가 아니다 — 아직 안 올라간 곡이다.</summary>
    NotFound,

    /// <summary>물어봤는데 답을 못 받았다(주소·네트워크·타임아웃·인증). 이건 알려야 한다.</summary>
    Failed,
}

/// <summary>곡에 딸린 것들 + 그것을 못 받았다면 왜 못 받았는지.</summary>
public readonly record struct SongExtrasResult(ExtrasReach Reach, SongExtras? Extras)
{
    public static readonly SongExtrasResult NotFound = new(ExtrasReach.NotFound, null);
    public static readonly SongExtrasResult Failed = new(ExtrasReach.Failed, null);

    public static SongExtrasResult Ok(SongExtras extras) => new(ExtrasReach.Ok, extras);
}

/// <summary>
/// 곡 하나에 딸린 것들. 가사가 아니라 <b>가사 옆에 붙는 것</b>이라 없어도 아무것도 깨지지 않는다.
/// </summary>
/// <param name="CoverUrl">앨범 커버 주소(없으면 null — 찾아봤지만 없는 곡도 많다).</param>
/// <param name="CoverSource">커버를 준 곳(<c>itunes</c> | <c>deezer</c>). 출처 표기·진단용.</param>
/// <param name="LastFmUrl">Last.fm이 알려 준 정식 곡 주소(없으면 null).</param>
/// <param name="LoveConnected">서버에 Last.fm 계정이 연결돼 있는가. false면 좋아요 UI를 감춘다.</param>
/// <param name="LoveKnown">
/// 좋아요 여부를 실제로 확인했는가. false면 <paramref name="Loved"/>를 믿으면 안 된다 —
/// 모르는 것을 꺼진 하트로 그리면 사람이 눌러서 이미 켜 둔 것을 끈다.
/// </param>
/// <param name="Loved">좋아요 상태(<paramref name="LoveKnown"/>이 true일 때만 의미가 있다).</param>
/// <param name="SpotifyConnected">서버에 Spotify 계정이 연결돼 있는가(false면 그 표시를 감춘다).</param>
/// <param name="SpotifyKnown">라이브러리 저장 여부를 실제로 확인했는가 — false면 믿지 말 것.</param>
/// <param name="SpotifySaved">Spotify 라이브러리에 담겨 있는가.</param>
/// <param name="Key">
/// 서버가 이 곡에 붙인 키. 있으면 <c>{서버 주소}/song?key=…</c>로 관리 화면을 바로 열 수 있다.
/// 키 규칙은 전적으로 서버 몫이라 <b>클라이언트가 만들어 쓰면 안 된다</b>.
/// </param>
public sealed record SongExtras(
    string? CoverUrl,
    string? CoverSource,
    string? LastFmUrl,
    bool LoveConnected,
    bool LoveKnown,
    bool Loved,
    string? Key = null,
    bool SpotifyConnected = false,
    bool SpotifyKnown = false,
    bool SpotifySaved = false)
{
    /// <summary>하트를 켜서 그려도 되는가 — 확인한 값일 때만.</summary>
    public bool ShowLoved => LoveKnown && Loved;

    /// <summary>Spotify 저장 표시를 켜서 그려도 되는가 — 확인한 값일 때만(좋아요와 같은 규칙).</summary>
    public bool ShowSpotifySaved => SpotifyKnown && SpotifySaved;

    /// <summary>
    /// 이 곡의 관리 화면 주소. 서버 주소를 받아 만든다(앱은 서버가 어디 있는지만 안다).
    /// 키를 모르거나 서버 주소가 없으면 null — 호출자는 버튼을 감춘다.
    /// </summary>
    public string? AdminUrl(string? serverEndpoint) =>
        string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(serverEndpoint)
            ? null
            : $"{serverEndpoint!.TrimEnd('/')}/song?key={Uri.EscapeDataString(Key!)}";
}

/// <summary>의미 생성 요청의 결과.</summary>
/// <param name="Status">무슨 일이 있었는지 — 사람에게 할 말이 이것으로 갈린다.</param>
/// <param name="Meaning">만들어졌을 때의 본문(그 외에는 null).</param>
public readonly record struct MeaningRequestResult(MeaningRequestStatus Status, SongMeaningView? Meaning)
{
    public static MeaningRequestResult Of(MeaningRequestStatus status) => new(status, null);
}

/// <summary>
/// 생성 요청의 결말. 서버가 돌려주는 <c>status</c>와 클라이언트 쪽 사정(미설정·미접속)을 합친 것이다.
/// </summary>
public enum MeaningRequestStatus
{
    /// <summary>만들었다 — <see cref="MeaningRequestResult.Meaning"/>에 본문이 있다.</summary>
    Created,
    /// <summary>외부 자료를 하나도 못 찾았다. 흔한 결과이고 실패가 아니다.</summary>
    NoSource,
    /// <summary>자료는 있었지만 그것만으로 의미를 말할 수 없었다.</summary>
    Insufficient,
    /// <summary>쿼타·네트워크로 잠시 안 된다. <b>저장하지 않았으므로 다시 눌러도 된다.</b></summary>
    Retry,
    /// <summary>
    /// 서버에 의미 엔진이 구성돼 있지 않거나, 앱 생성이 꺼져 있거나, 엔진 설정(결제 잔액·키·모델)에
    /// 문제가 있다(`202 {"status":"config"}`). 어느 쪽이든 앱이 아니라 서버를 고쳐야 한다.
    /// </summary>
    Unavailable,
    /// <summary>서버에 못 붙었거나 그 곡이 서버에 없다.</summary>
    Failed,
}

/// <summary>
/// 앱이 보여 줄 "이 곡의 의미" 한 건.
///
/// <see cref="Attribution"/>은 <b>표시 의무</b>가 있다 — Wikipedia 본문은 CC BY-SA이고
/// Genius·Last.fm도 링크 표기를 요구한다(`contracts/lyrics-api.md`). 본문만 떼어 보여 주면 안 된다.
/// </summary>
/// <param name="Summary">대상 언어 한 문단.</param>
/// <param name="Attribution">출처 이름·링크 쌍.</param>
/// <param name="Lang">요약 언어(`ko` 등).</param>
public sealed record SongMeaningView(
    string Summary,
    IReadOnlyList<MeaningCredit> Attribution,
    string Lang)
{
    /// <summary>화면 하단에 그대로 붙일 한 줄(링크가 없는 UI용).</summary>
    public string CreditLine =>
        Attribution.Count == 0
            ? ""
            : "출처: " + string.Join(" · ", Attribution.Select(a => a.Name))
              + (Attribution.Any(a => a.Name == "Wikipedia") ? " (CC BY-SA)" : "");
}

/// <summary>출처 한 건.</summary>
public sealed record MeaningCredit(string Name, string? Url);
