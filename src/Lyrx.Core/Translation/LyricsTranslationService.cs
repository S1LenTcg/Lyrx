namespace Musebase.Core.Translation;

/// <summary>翻译管线单次执行统计（遥测/诊断用）。不包含歌曲信息。</summary>
public sealed class TranslationRunStats
{
    /// <summary>本次执行中需要目标语言翻译的行数（缓存命中 + API，含重复行）。</summary>
    public int LinesNeeded { get; internal set; }

    /// <summary>其中由行缓存填充的数量。</summary>
    public int CacheHits { get; internal set; }

    /// <summary>缓存命中率（0–100 整数）。没有需要翻译的行时为 0。</summary>
    public int CacheHitPct => LinesNeeded == 0 ? 0 : (int)Math.Round(CacheHits * 100.0 / LinesNeeded);
}

/// <summary>
/// 歌词双语保障服务。
///
/// 策略（PRD）：
/// - 未配置翻译器：只用提供方翻译（"tr"）——不做任何事。
/// - 已配置翻译器：为所有非空行保证 "tr:{target}" 标签
///   （缓存优先，仅未命中的行批量翻译）。
/// - 显示层按 Translation("{target}") → "tr"（提供方）的顺序回退，因此
///   有目标语言机翻时用它，没有时显示提供方翻译。
/// </summary>
public sealed class LyricsTranslationService
{
    private readonly ITranslator? _translator;
    private readonly ITranslationCache _cache;

    public LyricsTranslationService(ITranslator? translator, ITranslationCache? cache = null)
    {
        _translator = translator;
        _cache = cache ?? new InMemoryTranslationCache();
    }

    /// <summary>翻译器是否已配置（是否输入了密钥）</summary>
    public bool IsEnabled => _translator is not null;

    /// <summary>
    /// 为 true 时不调用 API，只填充<b>已缓存的翻译</b>（已翻译过的行照常显示）。
    /// 用于让用户能立即停止付费 API 用量的开关——引擎/密钥设置保持不变，
    /// 只阻止新的翻译请求。
    /// </summary>
    public bool CacheOnly { get; init; }

    /// <summary>
    /// 创建此服务的翻译引擎 id（注册表 id，遥测用）。
    /// 由工厂（LyricsEngineFactory）按配置值填充。默认 "none"。
    /// </summary>
    public string EngineId { get; init; } = TranslatorRegistry.None;

    /// <summary>
    /// 为歌词填充目标语言翻译。返回值：发生变更的行数。
    /// 失败（网络/密钥错误）时静默返回 0——属于功能降级，不是错误。
    /// 传入 <paramref name="stats"/> 时统计需要翻译的行数与缓存命中（遥测用）。
    /// </summary>
    public async Task<int> EnsureTranslatedAsync(
        Lyrics lyrics, string targetLang, CancellationToken ct = default, TranslationRunStats? stats = null,
        bool preserveDefaultTranslation = false, Action? onBatchTranslated = null)
    {
        if (_translator is null) return 0;

        var tag = LineAttachments.TranslationTag(targetLang.ToLowerInvariant());
        var changed = 0;

        // 1) 应用缓存 + 收集需要翻译的行（重复原文只请求一次）
        var pending = new List<LyricsLine>();
        var pendingTexts = new List<string>();
        var seen = new Dictionary<string, List<LyricsLine>>();

        foreach (var line in lyrics.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Content)) continue;
            if (line.Attachments[tag] is not null) continue;
            if (preserveDefaultTranslation
                && !string.IsNullOrWhiteSpace(line.Attachments[LineAttachments.TagTranslationPrefix]))
                continue;

            if (stats is not null) stats.LinesNeeded++;

            if (_cache.Get(line.Content, targetLang) is { } cached)
            {
                line.Attachments[tag] = cached;
                changed++;
                if (stats is not null) stats.CacheHits++;
                continue;
            }

            if (seen.TryGetValue(line.Content, out var duplicates))
            {
                duplicates.Add(line);
            }
            else
            {
                seen[line.Content] = [line];
                pending.Add(line);
                pendingTexts.Add(line.Content);
            }
        }

        // 仅使用缓存模式在此结束——未命中的行不翻译（网络调用 0）。
        if (pendingTexts.Count == 0 || CacheOnly) return changed;

        // 若已有部分缓存命中，先通知 UI 刷新已命中的行
        if (changed > 0) onBatchTranslated?.Invoke();

        // 2) 分批翻译未命中的行，每批完成后立即写回并通知 UI 增量显示
        const int batchSize = 15;
        for (var offset = 0; offset < pendingTexts.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, pendingTexts.Count - offset);
            var batchTexts = pendingTexts.GetRange(offset, count);

            IReadOnlyList<string?> translated;
            try
            {
                translated = await _translator.TranslateAsync(batchTexts, targetLang, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return changed; // 静默降级
            }

            var batchChanged = 0;
            for (var i = 0; i < count && i < translated.Count; i++)
            {
                if (translated[i] is not { Length: > 0 } result) continue;
                var sourceText = batchTexts[i];
                foreach (var line in seen[sourceText])
                {
                    line.Attachments[tag] = result;
                    changed++;
                    batchChanged++;
                }
                try { _cache.Set(sourceText, targetLang, result); } catch { }
            }

            if (batchChanged > 0) onBatchTranslated?.Invoke();
        }

        return changed;
    }
}
