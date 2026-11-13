namespace Musebase.Core.Translation;

/// <summary>번역기 생성에 필요한 옵션(엔진별로 필요한 것만 사용).</summary>
public sealed record TranslatorOptions(
    string? DeeplApiKey = null,
    string? LibreEndpoint = null,
    string? LibreApiKey = null,
    string? MyMemoryEmail = null,
    string? GoogleApiKey = null,
    string? OpenRouterApiKey = null,
    /// <summary>비우면 <see cref="OpenRouterTranslator.DefaultModel"/>. 예: <c>anthropic/claude-opus-5</c>.</summary>
    string? OpenRouterModel = null,
    /// <summary>
    /// 번역기가 쓸 <see cref="HttpClient"/>. 비우면 각 번역기의 기본값
    /// (<c>LyricsHttp.Client</c> — 타임아웃 15초)이라 <b>앱 동작은 그대로다</b>.
    ///
    /// 서버의 일괄 번역처럼 <b>오래 걸려도 되는</b> 호출에는 여유 있는 클라이언트를 넣는다.
    /// 15초는 재생 중 화면 응답성에 맞춰 둔 값이라 LLM 번역의 예산(OpenRouter는 기본 60초,
    /// 최대 180초)을 몰래 자른다 — 의미 생성 쪽이 같은 이유로 전용 클라이언트를 따로 둔다
    /// (<c>MeaningHttp</c>).
    /// </summary>
    HttpClient? Http = null,
    /// <summary>
    /// OpenRouter 모델이 받아 주는 출력 토큰 상한. 비우면 번역기 기본값(8000).
    /// 모델마다 다르고(예: Tencent HY-MT2 계열은 4096) 넘기면 응답이 잘려 그 묶음이 통째로
    /// 버려지므로, 아는 값이 있으면 넣는다 — 묶음 크기가 여기서 자동으로 파생된다.
    /// </summary>
    int? OpenRouterMaxOutputTokens = null);

/// <summary>
/// 번역 엔진 설명자. 키 필요 여부·무료 여부(UI/기본값 판단)와 생성 팩토리.
/// <c>RequiresApiKey</c>=키가 없으면 동작 불가(팩토리가 null),
/// <c>AcceptsApiKey</c>=키 없이도 되지만 넣으면 사용(예: LibreTranslate 공개 인스턴스),
/// <c>ShortName</c>="{engine} API 키" 같은 UI 문구에 넣을 짧은 이름(비면 DisplayName).
/// </summary>
public sealed record TranslatorDescriptor(
    string Id,
    string DisplayName,
    bool RequiresApiKey,
    bool IsFree,
    Func<TranslatorOptions, ITranslator?> Factory,
    bool AcceptsApiKey = false,
    string? ShortName = null)
{
    /// <summary>API 키 입력을 노출할 엔진인지(필수 또는 선택).</summary>
    public bool UsesApiKey => RequiresApiKey || AcceptsApiKey;

    /// <summary>문구 삽입용 짧은 이름(괄호 수식 없는 제품명).</summary>
    public string Name => string.IsNullOrWhiteSpace(ShortName) ? DisplayName : ShortName!;
}

/// <summary>
/// 번역 엔진 레지스트리. 새 엔진은 <see cref="All"/>에 한 줄 추가로 편입된다.
/// 설정(<c>TranslationEngine</c>)으로 선택하고 <see cref="TranslatorOptions"/>로 키/엔드포인트를 주입한다.
/// 키 부족/미지원이면 팩토리가 null을 반환 → 번역 비활성(제공자 번역만).
/// </summary>
public static class TranslatorRegistry
{
    /// <summary>번역 비활성 식별자.</summary>
    public const string None = "none";

    /// <summary>무키 무료 기본 엔진(설치 직후 바로 동작). LibreTranslate 공개 인스턴스가
    /// 유료 키 필수로 바뀌어(2026) MyMemory를 기본 무키 엔진으로 사용한다.</summary>
    public const string DefaultFreeEngine = "mymemory";

    /// <summary>LibreTranslate 기본 엔드포인트(설정으로 자체호스팅 인스턴스로 교체 가능).</summary>
    public const string DefaultLibreEndpoint = "https://libretranslate.com";

    public static IReadOnlyList<TranslatorDescriptor> All { get; } = new TranslatorDescriptor[]
    {
        new("mymemory", "MyMemory (무료·무키)", RequiresApiKey: false, IsFree: true,
            o => new MyMemoryTranslator(o.MyMemoryEmail, o.Http),
            ShortName: "MyMemory"),
        new("libretranslate", "LibreTranslate (자체호스팅/키)", RequiresApiKey: false, IsFree: true,
            o => new LibreTranslateTranslator(
                string.IsNullOrWhiteSpace(o.LibreEndpoint) ? DefaultLibreEndpoint : o.LibreEndpoint!,
                o.LibreApiKey, o.Http),
            AcceptsApiKey: true, ShortName: "LibreTranslate"),
        new("deepl", "DeepL", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.DeeplApiKey) ? null : new DeeplTranslator(o.DeeplApiKey!, o.Http),
            ShortName: "DeepL"),
        new("google", "Google Cloud Translation (API 키)", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.GoogleApiKey)
                ? null
                : new GoogleTranslateTranslator(o.GoogleApiKey!, o.Http),
            ShortName: "Google Cloud Translation"),
        new("openrouter", "OpenRouter (LLM·모델 자유 선택)", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.OpenRouterApiKey)
                ? null
                : new OpenRouterTranslator(
                    o.OpenRouterApiKey!, o.OpenRouterModel, o.Http,
                    maxOutputTokens: o.OpenRouterMaxOutputTokens),
            ShortName: "OpenRouter"),
    };

    public static TranslatorDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>선택된 엔진의 번역기를 만든다. "none"/미지원/키 부족이면 null.</summary>
    public static ITranslator? Build(string id, TranslatorOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, None, StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(id)?.Factory(options);
    }
}
