namespace Musebase.Core.Translation;

/// <summary>机器翻译后端（DeepL 等）</summary>
public interface ITranslator
{
    /// <summary>
    /// 把文本数组翻译成目标语言。返回与输入等长的数组，
    /// 单条失败项为 null。
    /// </summary>
    Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default);
}

/// <summary>按行翻译缓存。键 = (原文, 目标语言)。</summary>
public interface ITranslationCache
{
    string? Get(string text, string targetLang);
    void Set(string text, string targetLang, string translation);
}

/// <summary>用于测试/不使用缓存场景的内存缓存</summary>
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
