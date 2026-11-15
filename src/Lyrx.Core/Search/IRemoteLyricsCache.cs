namespace Musebase.Core.Search;

/// <summary>
/// 远端查询一次的结果。即便未命中，也可能附带"其他设备此刻正在找同一首歌"的提示，
/// 因此单靠 <c>Lyrics?</c> 不够(`contracts/lyrics-api.md` 里的"翻译让步")。
/// </summary>
/// <param name="Lyrics">收到的歌词。未命中·失败则为 null。</param>
/// <param name="Pending">最近别的设备也问过这首歌 —— 值得暂时让出翻译。</param>
/// <param name="RetryAfterMs">服务器建议的重新查询间隔(0 表示无建议)。由调用方 clamp。</param>
/// <param name="Langs">收到的歌词中包含的翻译语言(小写)。非命中时为空数组。</param>
/// <param name="IsAd">
/// 服务器已把这个标题标记为<b>广告</b>。不应对提供方做搜索 —— 广告不是歌曲，
/// 搜索只会白跑，偶尔凑巧匹配就会把不相干的歌词盖在广告上。
/// </summary>
public readonly record struct RemoteLyricsResult(
    Lyrics? Lyrics, bool Pending, int RetryAfterMs, IReadOnlyList<string> Langs, bool IsAd = false)
{
    /// <summary>未命中·失败·未连接 —— 没有任何提示。</summary>
    public static readonly RemoteLyricsResult Miss = new(null, false, 0, []);

    /// <summary>服务器标记为广告的标题 —— 不搜索。</summary>
    public static readonly RemoteLyricsResult Ad = new(null, false, 0, [], IsAd: true);

    /// <summary>是否包含目标语言翻译(忽略大小写)。</summary>
    public bool HasLanguage(string lang) =>
        Langs.Contains(lang, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 远端歌词缓存(个人服务器)的契约。它是夹在本地缓存与提供方搜索之间的**增值层** ——
/// 没有服务器或连不上时必须安静地降级回原有行为(提供方搜索)，
/// 因此实现**不向外抛异常**(失败·超时·未连接 = <see cref="RemoteLyricsResult.Miss"/> / 忽略存储)。
///
/// HTTP 契约见 `contracts/lyrics-api.md`(v1)，参考实现见 <see cref="HttpRemoteLyricsCache"/>。
/// </summary>
public interface IRemoteLyricsCache
{
    /// <summary>从服务器取回歌词。未命中·失败·超时全部为 <see cref="RemoteLyricsResult.Miss"/>。</summary>
    Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>把歌词上传到服务器(upsert)。失败会被安静忽略，调用方不 await 也可以。</summary>
    Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default);

    /// <summary>
    /// 取回歌曲的含义。没有则为 null —— 这个调用<b>不会去生成</b>。
    /// 与歌词一样，失败·未连接也安静地返回 null。
    /// </summary>
    Task<SongMeaningView?> GetMeaningAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>
    /// 含义查询 + "服务器正在生成"的提示(404 响应体里的 <c>pending</c>)。新上架的歌曲服务器会
    /// 自动生成，所以收到提示就过一会儿再问(<see cref="MeaningFetch"/>)。
    /// 默认实现不认识该提示 —— 与未实现的测试替身·旧版本保持完全兼容。
    /// </summary>
    async Task<MeaningLookup> LookupMeaningAsync(string title, string artist, CancellationToken ct = default) =>
        new(await GetMeaningAsync(title, artist, ct).ConfigureAwait(false), false, 0);

    /// <summary>
    /// 请求服务器<b>现在就生成</b>这首歌的含义。只在人按下按钮时调用 ——
    /// 一次要多次外部 API + LLM 调用，很贵(禁止自动调用)。
    ///
    /// 与查询不同，可能要几十秒，所以用<b>长得多的超时</b>，失败也不会计入
    /// 熔断器(因为附加功能把歌词查询堵住，损失太大)。
    /// </summary>
    /// <param name="force">
    /// <b>丢弃已有的含义重新生成。</b>默认值(false)时，若服务器已经生成过，
    /// 就原样返回 —— 不重复昂贵的调用，尤其是<b>不覆盖现有文字</b>。
    /// 只在人点了"重新生成"并确认后才给 true。
    /// </param>
    Task<MeaningRequestResult> RequestMeaningAsync(
        string title, string artist, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// 一次取回歌曲附带的东西(封面地址·喜欢状态)。
    ///
    /// 取不到时<b>一并给出原因</b>(<see cref="ExtrasReach"/>) —— 首次播放的歌曲还没人
    /// 上传过，404 是正常的，把它画成连接错误，每首新歌都会弹警告。
    ///
    /// 服务器对还没找过封面的歌曲会<b>在这次调用里去查找</b>(每首一次)。因此
    /// 可能比歌词查询更慢，不要卡住界面，收到什么就填进去。
    /// </summary>
    Task<SongExtrasResult> GetExtrasAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>让封面从头重新查找(改过歌名之后)。只在人按下时调用。</summary>
    Task<SongExtras?> RefreshCoverAsync(string title, string artist, CancellationToken ct = default);

    /// <summary>打开或关闭 Last.fm 喜欢。失败则 null —— 不应改变界面。</summary>
    Task<SongExtras?> SetLovedAsync(string title, string artist, bool loved, CancellationToken ct = default);
}

/// <summary>
/// 询问服务器的结果。<b>"服务器不认识这首歌"和"没能问到服务器"是不同的</b> ——
/// 两者值都为空，但对人要说的话正相反。
///
/// 首次播放的歌曲还没人上传过，<see cref="NotFound"/> 是正常的(几秒后本机
/// 会上传)。把它画成错误，<b>每首正常的新歌都会弹警告</b>。
/// </summary>
public enum ExtrasReach
{
    /// <summary>取回来了。</summary>
    Ok,

    /// <summary>服务器答了但不认识这首歌(404)。不是错误 —— 这首歌还没上传。</summary>
    NotFound,

    /// <summary>问了但没收到回答(地址·网络·超时·认证)。这个必须告知。</summary>
    Failed,
}

/// <summary>歌曲附带的东西 + 若没取到则说明为何没取到。</summary>
public readonly record struct SongExtrasResult(ExtrasReach Reach, SongExtras? Extras)
{
    public static readonly SongExtrasResult NotFound = new(ExtrasReach.NotFound, null);
    public static readonly SongExtrasResult Failed = new(ExtrasReach.Failed, null);

    public static SongExtrasResult Ok(SongExtras extras) => new(ExtrasReach.Ok, extras);
}

/// <summary>
/// 一首歌附带的东西。不是歌词，而是<b>贴在歌词旁边的</b>，没有也不会坏什么。
/// </summary>
/// <param name="CoverUrl">专辑封面地址(没有则 null —— 找过但确实没有的歌也很多)。</param>
/// <param name="CoverSource">封面的来源(<c>itunes</c> | <c>deezer</c>)。用于标注出处·诊断。</param>
/// <param name="LastFmUrl">Last.fm 告知的正式歌曲地址(没有则 null)。</param>
/// <param name="LoveConnected">服务器上是否连了 Last.fm 账号。false 则隐藏喜欢 UI。</param>
/// <param name="LoveKnown">
/// 是否真的确认过喜欢状态。false 时不能相信 <paramref name="Loved"/> ——
/// 把不知道的画成未点亮的爱心，人会点一下，反而把已开的关掉。
/// </param>
/// <param name="Loved">喜欢状态(只有 <paramref name="LoveKnown"/> 为 true 时才有意义)。</param>
/// <param name="SpotifyConnected">服务器上是否连了 Spotify 账号(false 则隐藏该标记)。</param>
/// <param name="SpotifyKnown">是否真的确认过是否存入媒体库 —— false 时不要相信。</param>
/// <param name="SpotifySaved">是否已在 Spotify 媒体库里。</param>
/// <param name="Key">
/// 服务器为这首歌分配的键。有它就能用 <c>{服务器地址}/song?key=…</c> 直接打开管理页。
/// 键的规则完全属于服务器，<b>客户端不能自己造</b>。
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
    /// <summary>是否可以把爱心画成点亮 —— 只在确认过的值上。</summary>
    public bool ShowLoved => LoveKnown && Loved;

    /// <summary>是否可以画成已存入 Spotify —— 只在确认过的值上(与喜欢同一规则)。</summary>
    public bool ShowSpotifySaved => SpotifyKnown && SpotifySaved;

    /// <summary>
    /// 这首歌的管理页地址。用服务器地址拼出来(应用只知道服务器在哪)。
    /// 不知道键或没有服务器地址则为 null —— 调用方隐藏按钮。
    /// </summary>
    public string? AdminUrl(string? serverEndpoint) =>
        string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(serverEndpoint)
            ? null
            : $"{serverEndpoint!.TrimEnd('/')}/song?key={Uri.EscapeDataString(Key!)}";
}

/// <summary>含义生成请求的结果。</summary>
/// <param name="Status">发生了什么 —— 要对人说的话由它决定。</param>
/// <param name="Meaning">生成时的正文(其他情况为 null)。</param>
public readonly record struct MeaningRequestResult(MeaningRequestStatus Status, SongMeaningView? Meaning)
{
    public static MeaningRequestResult Of(MeaningRequestStatus status) => new(status, null);
}

/// <summary>
/// 生成请求的结局。是服务器返回的 <c>status</c> 与客户端侧情况(未配置·未连接)的合并结果。
/// </summary>
public enum MeaningRequestStatus
{
    /// <summary>已生成 —— <see cref="MeaningRequestResult.Meaning"/> 里有正文。</summary>
    Created,
    /// <summary>一份外部资料都没找到。常见结果，不是失败。</summary>
    NoSource,
    /// <summary>有资料，但光靠它说不清含义。</summary>
    Insufficient,
    /// <summary>配额·网络导致暂时不行。<b>因为没保存，可以再点一次。</b></summary>
    Retry,
    /// <summary>
    /// 服务器上未配置含义引擎，或应用生成被关闭，或引擎设置(付费余额·键·模型)有
    /// 问题(`202 {"status":"config"}`)。无论哪种，要改的是服务器而不是应用。
    /// </summary>
    Unavailable,
    /// <summary>没连上服务器，或服务器上没有这首歌。</summary>
    Failed,
}

/// <summary>
/// 应用要展示的"这首歌的含义"一条。
///
/// <see cref="Attribution"/> 有<b>标注义务</b> —— Wikipedia 正文是 CC BY-SA，
/// Genius·Last.fm 也要求标注链接(`contracts/lyrics-api.md`)。不能只截正文展示。
/// </summary>
/// <param name="Summary">目标语言的一段。</param>
/// <param name="Attribution">出处名称·链接对。</param>
/// <param name="Lang">摘要语言(`ko` 等)。</param>
public sealed record SongMeaningView(
    string Summary,
    IReadOnlyList<MeaningCredit> Attribution,
    string Lang)
{
    /// <summary>可直接贴在界面底部的一行(给没有链接的 UI 用)。</summary>
    public string CreditLine =>
        Attribution.Count == 0
            ? ""
            : "来源: " + string.Join(" · ", Attribution.Select(a => a.Name))
              + (Attribution.Any(a => a.Name == "Wikipedia") ? " (CC BY-SA)" : "");
}

/// <summary>一条出处。</summary>
public sealed record MeaningCredit(string Name, string? Url);
