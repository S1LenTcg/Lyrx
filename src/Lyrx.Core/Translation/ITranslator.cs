namespace Musebase.Core.Translation;

/// <summary>기계번역 백엔드 (DeepL 등)</summary>
public interface ITranslator
{
    /// <summary>
    /// 텍스트 배열을 대상 언어로 번역. 입력과 같은 길이의 배열을 반환하며
    /// 개별 실패 항목은 null.
    /// </summary>
    Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default);
}

/// <summary>라인 단위 번역 캐시. 키 = (원문, 대상 언어).</summary>
public interface ITranslationCache
{
    string? Get(string text, string targetLang);
    void Set(string text, string targetLang, string translation);
}

/// <summary>테스트/캐시 미사용 시나리오용 인메모리 캐시</summary>
public sealed class InMemoryTranslationCache : ITranslationCache
{
    private readonly Dictionary<(string, string), string> _store = new();
    private readonly object _lock = new();

    public string? Get(string text, string targetLang)
    {
        lock (_lock) return _store.GetValueOrDefault((text, targetLang));
    }

    public void Set(string text, string targetLang, string translation)
    {
        lock (_lock) _store[(text, targetLang)] = translation;
    }
}
