namespace Musebase.Core.Search;

/// <summary>歌词源描述符。包含授权性质(是否为官方 API)与创建工厂。</summary>
public sealed record LyricsSourceDescriptor(
    string Id,
    string DisplayName,
    bool IsOfficialApi,
    Func<ILyricsProvider> Factory);

/// <summary>
/// 歌词源注册表。新提供者只需在 <see cref="All"/> 中加一行即可接入。
/// 由设置(启用的 id 列表)组合后注入 <see cref="LyricsSearchService"/>。
/// 用 IsOfficialApi 元数据区分发布配置(公开=仅官方 / 个人=全部)。
/// </summary>
public static class LyricsSourceRegistry
{
    public static IReadOnlyList<LyricsSourceDescriptor> All { get; } = new LyricsSourceDescriptor[]
    {
        new("lrclib",  "LRCLIB",   IsOfficialApi: true,  () => new LrclibProvider()),
        new("netease", "NetEase",  IsOfficialApi: false, () => new NetEaseProvider()),
        new("kugou",   "Kugou",    IsOfficialApi: false, () => new KugouProvider()),
        new("qqmusic", "QQ Music", IsOfficialApi: false, () => new QQMusicProvider()),
    };

    /// <summary>已注册的所有源 id(按注册顺序)。</summary>
    public static IReadOnlyList<string> AllIds { get; } = All.Select(d => d.Id).ToList();

    /// <summary>仅官方 API 源 id(公开版安全配置)。</summary>
    public static IReadOnlyList<string> OfficialIds { get; } =
        All.Where(d => d.IsOfficialApi).Select(d => d.Id).ToList();

    public static LyricsSourceDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>按注册顺序为 enabledIds 中对应的提供者创建实例。</summary>
    public static ILyricsProvider[] Build(IEnumerable<string> enabledIds)
    {
        var set = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        return All.Where(d => set.Contains(d.Id)).Select(d => d.Factory()).ToArray();
    }
}
