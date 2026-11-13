namespace Musebase.Core.Search;

/// <summary>가사 소스 설명자. 라이선스 성격(공식 API 여부)과 생성 팩토리를 담는다.</summary>
public sealed record LyricsSourceDescriptor(
    string Id,
    string DisplayName,
    bool IsOfficialApi,
    Func<ILyricsProvider> Factory);

/// <summary>
/// 가사 소스 레지스트리. 새 제공자는 <see cref="All"/>에 한 줄 추가로 편입된다.
/// 설정(활성 id 목록)으로 조합해 <see cref="LyricsSearchService"/>에 주입한다.
/// IsOfficialApi 메타로 배포 프로파일(공개=공식만 / 개인=전부)을 나눈다.
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

    /// <summary>등록된 모든 소스 id(등록 순).</summary>
    public static IReadOnlyList<string> AllIds { get; } = All.Select(d => d.Id).ToList();

    /// <summary>공식 API 소스 id만(공개 배포 안전 프로파일).</summary>
    public static IReadOnlyList<string> OfficialIds { get; } =
        All.Where(d => d.IsOfficialApi).Select(d => d.Id).ToList();

    public static LyricsSourceDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>enabledIds에 해당하는 제공자 인스턴스를 등록 순서대로 생성한다.</summary>
    public static ILyricsProvider[] Build(IEnumerable<string> enabledIds)
    {
        var set = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        return All.Where(d => set.Contains(d.Id)).Select(d => d.Factory()).ToArray();
    }
}
