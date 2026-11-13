namespace Musebase.Core.Meaning;

/// <summary>의미 생성 엔진 구성값. 키가 없는 엔진은 만들어지지 않는다(null).</summary>
public sealed record MeaningWriterOptions
{
    public string? GeminiApiKey { get; init; }
    public string? GeminiModel { get; init; }
    public string? OpenRouterApiKey { get; init; }
    public string? OpenRouterModel { get; init; }
    public HttpClient? Http { get; init; }
}

/// <summary>표시·선택용 엔진 서술자.</summary>
public sealed record MeaningWriterDescriptor(
    string Id,
    string Display,
    Func<MeaningWriterOptions, IMeaningWriter?> Factory);

/// <summary>
/// 의미 생성 엔진 레지스트리. <see cref="Translation.TranslatorRegistry"/>와 같은 모양이라
/// 설정 화면·환경변수 배선이 그대로 재사용된다.
///
/// 기본은 <c>none</c>이다 — 키를 넣기 전에는 아무것도 하지 않고, 관리자 화면에는 외부 링크만 뜬다.
/// </summary>
public static class MeaningWriterRegistry
{
    public const string None = "none";

    public static IReadOnlyList<MeaningWriterDescriptor> All { get; } = new MeaningWriterDescriptor[]
    {
        new("gemini", "Google Gemini (API 키·무료 티어 있음)",
            o => string.IsNullOrWhiteSpace(o.GeminiApiKey)
                ? null
                : new GeminiMeaningWriter(o.GeminiApiKey!, o.GeminiModel, o.Http)),

        new("openrouter", "OpenRouter (모델 자유 선택)",
            o => string.IsNullOrWhiteSpace(o.OpenRouterApiKey)
                ? null
                : new OpenRouterMeaningWriter(o.OpenRouterApiKey!, o.OpenRouterModel, o.Http)),
    };

    public static MeaningWriterDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>선택된 엔진을 만든다. "none"/미지원/키 부족이면 null.</summary>
    public static IMeaningWriter? Build(string? id, MeaningWriterOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, None, StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(id!)?.Factory(options);
    }
}
