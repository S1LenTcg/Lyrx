using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Engine;

/// <summary>
/// 플랫폼 무관 엔진 구성값. 설정(App)에서 채워 팩토리에 넘긴다.
/// Windows/Android/서버가 같은 구성으로 코디네이터를 조립한다.
/// </summary>
public sealed record EngineConfig(
    IReadOnlyList<string> EnabledLyricsSources,
    string TranslationEngineId,
    TranslatorOptions TranslatorOptions,
    string TargetLanguage,
    bool ShowOnlyTargetTranslation,
    double ManualOffsetSeconds,
    string CacheDbPath,
    // 주 번역 엔진 실패 시 폴백할 엔진 id(예: "libretranslate"). null=폴백 없음.
    string? TranslationFallbackEngineId = null,
    // false면 번역 API를 호출하지 않고 캐시된 번역만 표시한다(유료 API 사용량 차단 토글).
    // 엔진/키 설정은 그대로 유지되므로 다시 켜면 즉시 원래대로 동작한다.
    bool ApiTranslationEnabled = true,
    // 개인 가사 서버(선택). 비면 사용하지 않는다 — 계약은 contracts/lyrics-api.md.
    string? LyricsServerEndpoint = null,
    string? LyricsServerToken = null,
    int LyricsServerTimeoutMs = 2500);

/// <summary>
/// 코어 서비스(가사 검색·번역·캐시)를 조합해 <see cref="LyricsCoordinator"/>를 만드는
/// 단일 조립 지점. Program의 뚱뚱한 배선을 대체하고, 동일 조합을 다른 플랫폼이 재사용한다.
/// 소스/엔진은 레지스트리에서 구성값에 따라 선택된다.
/// </summary>
public static class LyricsEngineFactory
{
    /// <summary>
    /// 구성·캐시로 번역 서비스를 만든다(엔진/키 변경 시 재구성용으로도 사용).
    /// 주 엔진 + (설정 시)폴백 엔진을 CompositeTranslator로 묶어, 주 엔진 실패 시
    /// 폴백으로 번역을 유지한다. 실패는 <paramref name="onFailure"/>로 보고(로깅·상태 힌트용).
    /// </summary>
    public static LyricsTranslationService BuildTranslation(
        EngineConfig config, ITranslationCache cache, Action<TranslatorFailure>? onFailure = null)
    {
        var chain = new List<(string, ITranslator)>();
        if (TranslatorRegistry.Build(config.TranslationEngineId, config.TranslatorOptions) is { } primary)
            chain.Add((config.TranslationEngineId, primary));

        // 번역을 끈 기기(none)는 폴백도 없다 — 폴백만으로 체인을 만들면 "끔인데 공개 서버로
        // 번역되는" 상태가 된다. 가사 서버가 있는 새 설치의 기본값이 none이라 더 중요하다.
        var fallbackId = string.Equals(config.TranslationEngineId, TranslatorRegistry.None, StringComparison.OrdinalIgnoreCase)
            ? null
            : config.TranslationFallbackEngineId;
        if (!string.IsNullOrWhiteSpace(fallbackId) &&
            !string.Equals(fallbackId, config.TranslationEngineId, StringComparison.OrdinalIgnoreCase) &&
            TranslatorRegistry.Build(fallbackId!, config.TranslatorOptions) is { } fb)
            chain.Add((fallbackId!, fb));

        // 체인이 비면(엔진 none/키 없음) 번역기 없음 = 서비스 비활성.
        ITranslator? translator = chain.Count == 0 ? null : new CompositeTranslator(chain, onFailure);
        return new LyricsTranslationService(translator, cache)
        {
            EngineId = config.TranslationEngineId, // translation 텔레메트리의 engine 식별용
            // API 번역 끔 → 캐시된 번역만 채우고 새 요청은 보내지 않는다(엔진/키 설정은 보존).
            CacheOnly = !config.ApiTranslationEnabled,
        };
    }

    /// <summary>
    /// 재생 소스·디스패처·구성으로 완전 배선된 코디네이터를 만든다.
    /// <paramref name="telemetry"/> 미주입(null) 시 NoopTelemetry — 수집하지 않는다(ADR-0004).
    /// </summary>
    public static LyricsCoordinator Create(
        INowPlayingSource source,
        IEngineDispatcher dispatcher,
        EngineConfig config,
        ITranslationCache translationCache,
        Action<string>? log = null,
        ITelemetry? telemetry = null,
        Action<TranslatorFailure>? onTranslationFailure = null)
    {
        var coordinator = new LyricsCoordinator(
            source, dispatcher, new LyricsSearchService(LyricsSourceRegistry.Build(config.EnabledLyricsSources)))
        {
            Cache = new LyricsCacheStore(config.CacheDbPath),
            TargetLanguage = config.TargetLanguage,
            ShowOnlyTargetTranslation = config.ShowOnlyTargetTranslation,
            ManualOffsetSeconds = config.ManualOffsetSeconds,
            Log = log,
            Telemetry = telemetry ?? NoopTelemetry.Instance,
        };
        ApplyTranslation(coordinator, config, translationCache, onTranslationFailure);
        ApplyServer(coordinator, config, log);
        return coordinator;
    }

    /// <summary>
    /// 가사 서버 구성을 (재)적용한다 — 주소/토큰 변경 시 호출(설정 저장 등). 주소가 비면 끈다.
    /// <see cref="LyricsCoordinator.RemoteCache"/>가 가변 속성이라 **재시작이 필요 없다**
    /// (가사 소스 목록은 생성자 주입이라 재시작이 필요한 것과 대조적).
    /// </summary>
    public static void ApplyServer(LyricsCoordinator coordinator, EngineConfig config, Action<string>? log = null)
    {
        var endpoint = config.LyricsServerEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            coordinator.RemoteCache = null;
            return;
        }

        try
        {
            coordinator.RemoteCache = new HttpRemoteLyricsCache(
                endpoint!.Trim(), config.LyricsServerToken, config.LyricsServerTimeoutMs, log);
            log?.Invoke($"[server] 가사 서버 사용: {endpoint!.Trim()}");
        }
        catch (Exception e)
        {
            // 주소 형식 오류 등 — 서버 없이 동작한다(기능 강등이지 오류가 아니다).
            coordinator.RemoteCache = null;
            log?.Invoke($"[server] 주소가 올바르지 않아 사용하지 않습니다: {e.Message}");
        }
    }

    /// <summary>
    /// 코디네이터의 번역 구성을 (재)적용한다 — 엔진/키/대상 언어 변경 시 호출(설정 저장 등).
    /// 번역기 실패를 코디네이터(번역 표시 상태)와 App(로깅·힌트) 양쪽으로 라우팅하고,
    /// 현재 라인을 즉시 재발행해 새 번역/상태가 바로 반영되게 한다.
    /// </summary>
    public static void ApplyTranslation(
        LyricsCoordinator coordinator,
        EngineConfig config,
        ITranslationCache translationCache,
        Action<TranslatorFailure>? onTranslationFailure = null)
    {
        coordinator.SetSearchService(new LyricsSearchService(LyricsSourceRegistry.Build(config.EnabledLyricsSources)));
        coordinator.Translation = BuildTranslation(config, translationCache, f =>
        {
            coordinator.RecordTranslationFailure(f);
            onTranslationFailure?.Invoke(f);
        });
        coordinator.TargetLanguage = config.TargetLanguage;
        coordinator.ShowOnlyTargetTranslation = config.ShowOnlyTargetTranslation;
        coordinator.RefreshCurrentLine();
    }
}
