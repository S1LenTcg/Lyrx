namespace Musebase.Engine;

/// <summary>
/// 광고 판정 규칙. 플랫폼 타입에 의존하지 않는 순수 함수라 Windows(SMTC)와 Android(MediaSession)가
/// **같은 코드**로 판정한다(원래 Musebase.Android에 있었고, 그 주석의 "코어로 옮긴다"를 이행한 것이다 —
/// 옮기면서 유닛 테스트가 붙는다. Musebase.Android는 sln 밖이라 CI에서 돌지 않는다).
///
/// 쓰임새는 두 가지다.
/// - Android: 광고 구간 자동 뮤트(옵인, ADR-0006).
/// - 공통: **광고 구간에는 가사를 찾지 않는다.** 광고는 곡이 아니라서 검색이 늘 헛돌고,
///   가사 서버에는 "Spotify / 광고 1/2" 같은 쓰레기 행이 쌓인다(실제로 쌓여 있었다).
///
/// 신호를 신뢰도 순으로 겹친다. 1·2번은 Spotify가 시스템 계약에 맞춰 내보내는 값이라
/// 지역·언어와 무관하고, 3번만 추측이다.
/// </summary>
public static class AdSignals
{
    /// <summary>
    /// 표준 광고 메타데이터 키. 플랫폼 상수(<c>MediaMetadata.METADATA_KEY_ADVERTISEMENT</c>)를
    /// 그대로 쓰지 못하고 문자열을 박은 이유: <b>.NET for Android 바인딩에 이 상수가 없다</b>
    /// (Microsoft.Android.Ref.34의 <c>Android.Media.MediaMetadata</c>에는 <c>MetadataKeyMediaId</c>
    /// 등은 있지만 <c>MetadataKeyAdvertisement</c>는 없다 — 직접 확인함).
    /// 값 자체는 안드로이드 플랫폼 계약이라 바뀌지 않는다. Windows(SMTC)에는 대응 개념이 없어
    /// 항상 0을 넘기게 된다.
    /// </summary>
    public const string AdvertisementMetadataKey = "android.media.metadata.ADVERTISEMENT";

    /// <summary>위 키가 광고를 뜻하는 값.</summary>
    private const long AdvertisementFlagSet = 1;

    /// <summary>Spotify가 광고 구간에 쓰는 미디어 ID 접두사. macOS 원본이 쓰던 것과 같은 신호.</summary>
    private const string SpotifyAdMediaIdPrefix = "spotify:ad";

    /// <summary>
    /// Windows(SMTC)에서 실측 검증된 폴백. 앨범 조건이 안전장치다 — Spotify의 실제 곡은 앨범이
    /// 항상 채워져 있으므로, 이 이름으로 발매된 진짜 곡이 있어도 광고로 오인되지 않는다.
    ///
    /// <b>안드로이드에서는 이 표가 맞지 않는다</b> — 실측 결과 광고의 아티스트는
    /// <c>'광고 • 1/2'</c>(현지화 + 순번)였다. 순번이 붙어 고정 문자열로 잡을 수 없고 언어마다
    /// 다르다. 그래도 지우지 않는 이유는 이게 신호 ③이기 때문이다: ①(플래그)과 ②(mediaId)가
    /// 둘 다 오는 것을 확인했으므로 실무상 여기까지 내려올 일이 없고, 만약 Spotify가 ①②를
    /// 빼면 최소한 다른 지역/버전에서 걸릴 여지를 남겨 둔다.
    /// </summary>
    private static readonly string[] AdArtists = { "Spotify", "Sponsored Message" };

    /// <param name="advertisementFlag">`METADATA_KEY_ADVERTISEMENT` 값(없으면 0 — Windows는 항상 0).</param>
    /// <param name="mediaId">`METADATA_KEY_MEDIA_ID` 값(Windows에는 없다 — null).</param>
    public static bool LooksLikeAd(long advertisementFlag, string? mediaId, string? artist, string? album)
    {
        // 1) 시스템 표준 광고 플래그 — 있으면 이게 결론이다.
        if (advertisementFlag == AdvertisementFlagSet) return true;

        // 2) Spotify 고유 광고 URI.
        if (!string.IsNullOrWhiteSpace(mediaId) &&
            mediaId!.TrimStart().StartsWith(SpotifyAdMediaIdPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3) 문자열 폴백. 앨범이 비어 있을 때만 본다.
        if (!string.IsNullOrWhiteSpace(album)) return false;
        if (string.IsNullOrWhiteSpace(artist)) return false;

        var trimmed = artist!.Trim();
        foreach (var candidate in AdArtists)
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 메타데이터가 없는 플랫폼(Windows SMTC)용 간편 오버로드 — 신호 ③만 본다.
    /// </summary>
    public static bool LooksLikeAd(string? artist, string? album) =>
        LooksLikeAd(0, null, artist, album);
}
