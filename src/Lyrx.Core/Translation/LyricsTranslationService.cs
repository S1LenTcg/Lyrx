namespace Musebase.Core.Translation;

/// <summary>번역 파이프라인 1회 실행 통계(텔레메트리·진단용). 곡 정보는 담지 않는다.</summary>
public sealed class TranslationRunStats
{
    /// <summary>이번 실행에서 대상 언어 번역이 필요했던 라인 수(캐시 적중 + API, 중복 라인 포함).</summary>
    public int LinesNeeded { get; internal set; }

    /// <summary>그중 라인 캐시로 채운 수.</summary>
    public int CacheHits { get; internal set; }

    /// <summary>캐시 적중률(0–100 정수). 필요 라인이 없으면 0.</summary>
    public int CacheHitPct => LinesNeeded == 0 ? 0 : (int)Math.Round(CacheHits * 100.0 / LinesNeeded);
}

/// <summary>
/// 가사 이중언어 보장 서비스.
///
/// 정책 (PRD):
/// - 번역기 미설정: 제공자 번역("tr")만 사용 — 아무것도 하지 않는다.
/// - 번역기 설정: 모든 비어있지 않은 라인에 "tr:{target}" 태그를 보장한다
///   (캐시 우선, 미스만 배치 번역).
/// - 표시 계층은 Translation("{target}") → "tr"(제공자) 순으로 폴백하므로
///   대상 언어 MT가 있으면 그것이, 없으면 제공자 번역이 보인다.
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

    /// <summary>번역기가 구성되어 있는가 (키 입력 여부)</summary>
    public bool IsEnabled => _translator is not null;

    /// <summary>
    /// true면 API를 호출하지 않고 <b>캐시된 번역만</b> 채운다(이미 번역해 둔 줄은 그대로 보인다).
    /// 유료 API 사용량을 사용자가 즉시 끊을 수 있게 하는 토글용 — 엔진/키 설정은 그대로 둔 채
    /// 새 번역 요청만 막는다.
    /// </summary>
    public bool CacheOnly { get; init; }

    /// <summary>
    /// 이 서비스를 만든 번역 엔진 id(레지스트리 id, 텔레메트리용).
    /// 팩토리(LyricsEngineFactory)가 구성값으로 채운다. 기본 "none".
    /// </summary>
    public string EngineId { get; init; } = TranslatorRegistry.None;

    /// <summary>
    /// 가사에 대상 언어 번역을 채운다. 반환값: 변경된 라인 수.
    /// 실패(네트워크/키 오류)는 조용히 0 — 기능 강등이지 오류가 아니다.
    /// <paramref name="stats"/>를 주면 필요 라인 수·캐시 적중을 집계한다(텔레메트리용).
    /// </summary>
    public async Task<int> EnsureTranslatedAsync(
        Lyrics lyrics, string targetLang, CancellationToken ct = default, TranslationRunStats? stats = null)
    {
        if (_translator is null) return 0;

        var tag = LineAttachments.TranslationTag(targetLang.ToLowerInvariant());
        var changed = 0;

        // 1) 캐시 적용 + 번역 필요 라인 수집 (중복 원문은 한 번만 요청)
        var pending = new List<LyricsLine>();
        var pendingTexts = new List<string>();
        var seen = new Dictionary<string, List<LyricsLine>>();

        foreach (var line in lyrics.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Content)) continue;
            if (line.Attachments[tag] is not null) continue;

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

        // 캐시만 사용 모드면 여기서 끝 — 미스는 번역하지 않는다(네트워크 호출 0).
        if (pendingTexts.Count == 0 || CacheOnly) return changed;

        // 2) 미스만 배치 번역
        IReadOnlyList<string?> translated;
        try
        {
            translated = await _translator.TranslateAsync(pendingTexts, targetLang, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return changed; // 조용한 강등
        }

        // 3) 결과 반영 + 캐시 (같은 원문의 중복 라인 포함)
        for (var i = 0; i < pending.Count && i < translated.Count; i++)
        {
            if (translated[i] is not { Length: > 0 } result) continue;
            _cache.Set(pendingTexts[i], targetLang, result);
            foreach (var line in seen[pendingTexts[i]])
            {
                line.Attachments[tag] = result;
                changed++;
            }
        }

        return changed;
    }
}
