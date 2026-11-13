namespace Musebase.Engine;

/// <summary>
/// 广告判定规则。它是不依赖平台类型的纯函数，因此 Windows(SMTC) 与 Android(MediaSession)
/// 用**同一份代码**判定（原本在 Musebase.Android 里，这条注释中说的"移到核心"已经落实 ——
/// 迁移时顺便补上了单元测试。Musebase.Android 不在 sln 里，所以 CI 不会跑到它）。
///
/// 用途有两种。
/// - Android：广告时段自动静音（可选项，ADR-0006）。
/// - 共通：**广告时段不去找歌词。** 广告不是歌曲，搜索总是白跑，
///   而歌词服务器上会堆积 "Spotify / 广告 1/2" 这类垃圾行（实际上确实堆过）。
///
/// 各类信号按可信度叠加。第 1、2 条是 Spotify 按系统契约发出的值，
/// 与地区、语言无关；只有第 3 条属于推测。
/// </summary>
public static class AdSignals
{
    /// <summary>
    /// 标准广告元数据键。之所以不能直接使用平台常量（<c>MediaMetadata.METADATA_KEY_ADVERTISEMENT</c>）
    /// 而要硬写字符串，是因为：<b>.NET for Android 的绑定里没有这个常量</b>
    /// （Microsoft.Android.Ref.34 的 <c>Android.Media.MediaMetadata</c> 里有 <c>MetadataKeyMediaId</c>
    /// 之类，却没有 <c>MetadataKeyAdvertisement</c> —— 已亲自核对）。
    /// 值本身属于 Android 平台契约，不会变。Windows(SMTC) 没有对应概念，
    /// 因此总是传 0。
    /// </summary>
    public const string AdvertisementMetadataKey = "android.media.metadata.ADVERTISEMENT";

    /// <summary>上面那个键表示"是广告"时的取值。</summary>
    private const long AdvertisementFlagSet = 1;

    /// <summary>Spotify 在广告时段使用的媒体 ID 前缀。与 macOS 原版所用的信号一致。</summary>
    private const string SpotifyAdMediaIdPrefix = "spotify:ad";

    /// <summary>
    /// 在 Windows(SMTC) 上实测验证过的兜底。专辑条件是安全阀 —— Spotify 的真实歌曲
    /// 专辑字段总是有值，所以即使真的存在以这个名字发行的歌曲，也不会被误判成广告。
    ///
    /// <b>这套表在 Android 上不成立</b> —— 实测发现广告的艺术家是
    /// <c>'广告 • 1/2'</c>（本地化 + 序号）。因为带序号，无法用固定字符串命中，而且每种语言
    /// 都不一样。之所以不删，是因为它属于信号 ③：已经确认 ①（标志位）和 ②（mediaId）
    /// 都会出现，所以实务上根本不会走到这里；万一 Spotify 去掉 ①②，
    /// 至少还留着在其他地区/版本上命中的余地。
    /// </summary>
    private static readonly string[] AdArtists = { "Spotify", "Sponsored Message" };

    /// <param name="advertisementFlag">`METADATA_KEY_ADVERTISEMENT` 的值（没有则为 0 —— Windows 永远是 0）。</param>
    /// <param name="mediaId">`METADATA_KEY_MEDIA_ID` 的值（Windows 没有 —— null）。</param>
    public static bool LooksLikeAd(long advertisementFlag, string? mediaId, string? artist, string? album)
    {
        // 1) 系统标准广告标志位 —— 有它就是定论。
        if (advertisementFlag == AdvertisementFlagSet) return true;

        // 2) Spotify 独有的广告 URI。
        if (!string.IsNullOrWhiteSpace(mediaId) &&
            mediaId!.TrimStart().StartsWith(SpotifyAdMediaIdPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3) 字符串兜底。只在专辑为空时才看。
        if (!string.IsNullOrWhiteSpace(album)) return false;
        if (string.IsNullOrWhiteSpace(artist)) return false;

        var trimmed = artist!.Trim();
        foreach (var candidate in AdArtists)
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 面向没有元数据的平台（Windows SMTC）的简化重载 —— 只看信号 ③。
    /// </summary>
    public static bool LooksLikeAd(string? artist, string? album) =>
        LooksLikeAd(0, null, artist, album);
}
