using Musebase.Core.Search;
using Musebase.Core.Translation;

namespace Musebase.Engine;

/// <summary>
/// 与平台无关的引擎配置值。由设置层（App）填充后交给工厂。
/// Windows/Android/服务端都用同一份配置组装协调器。
/// </summary>
public sealed record EngineConfig(
    IReadOnlyList<string> EnabledLyricsSources,
    string TranslationEngineId,
    TranslatorOptions TranslatorOptions,
    string TargetLanguage,
    bool ShowOnlyTargetTranslation,
    double ManualOffsetSeconds,
    string CacheDbPath,
    // 主翻译引擎失败时用于回退的引擎 id（例如 "libretranslate"）。null=不回退。
    string? TranslationFallbackEngineId = null,
    // 为 false 时不调用翻译 API，只显示已缓存的翻译（付费 API 用量拦截开关）。
    // 引擎/密钥设置保持不变，所以重新开启后立即恢复原样工作。
    bool ApiTranslationEnabled = true,
    // 个人歌词服务器（可选）。为空则不使用 —— 协议见 contracts/lyrics-api.md。
    string? LyricsServerEndpoint = null,
    string? LyricsServerToken = null,
    int LyricsServerTimeoutMs = 2500);

/// <summary>
/// 把核心服务（歌词搜索、翻译、缓存）组合成 <see cref="LyricsCoordinator"/> 的
/// 唯一装配点。取代 Program 里臃肿的接线，同一组合可被其他平台复用。
/// 来源/引擎按配置值从注册表中选择。
/// </summary>
public static class LyricsEngineFactory
{
    /// <summary>
    /// 用配置与缓存创建翻译服务（引擎/密钥变更时也用它重新构建）。
    /// 把主引擎 +（设置时）回退引擎用 CompositeTranslator 包起来，主引擎失败时
    /// 由回退保持翻译。失败通过 <paramref name="onFailure"/> 上报（用于日志、状态提示）。
    /// </summary>
    public static LyricsTranslationService BuildTranslation(
        EngineConfig config, ITranslationCache cache, Action<TranslatorFailure>? onFailure = null)
    {
        var chain = new List<(string, ITranslator)>();
        if (TranslatorRegistry.Build(config.TranslationEngineId, config.TranslatorOptions) is { } primary)
            chain.Add((config.TranslationEngineId, primary));

        // 关掉翻译的设备（none）也没有回退 —— 只用回退搭建链会变成"已关闭却
        // 从公开服务器翻译"的状态。有歌词服务器的新安装默认值是 none，所以这一点更重要。
        var fallbackId = string.Equals(config.TranslationEngineId, TranslatorRegistry.None, StringComparison.OrdinalIgnoreCase)
            ? null
            : config.TranslationFallbackEngineId;
        if (!string.IsNullOrWhiteSpace(fallbackId) &&
            !string.Equals(fallbackId, config.TranslationEngineId, StringComparison.OrdinalIgnoreCase) &&
            TranslatorRegistry.Build(fallbackId!, config.TranslatorOptions) is { } fb)
            chain.Add((fallbackId!, fb));

        // 链为空时（引擎 none / 无密钥）没有翻译器 = 服务不启用。
        ITranslator? translator = chain.Count == 0 ? null : new CompositeTranslator(chain, onFailure);
        return new LyricsTranslationService(translator, cache)
        {
            EngineId = config.TranslationEngineId, // 用于 translation 遥测的 engine 标识
            // 关闭 API 翻译 → 只填充已缓存的翻译，不发送新请求（引擎/密钥设置保留）。
            CacheOnly = !config.ApiTranslationEnabled,
        };
    }

    /// <summary>
    /// 用播放来源、调度器与配置创建完全接好线的协调器。
    /// <paramref name="telemetry"/> 未注入（null）时为 NoopTelemetry —— 不收集（ADR-0004）。
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
    /// （重新）应用歌词服务器配置 —— 地址/令牌变更时调用（保存设置等）。地址为空则关闭。
    /// <see cref="LyricsCoordinator.RemoteCache"/> 是可变属性，所以**无需重启**
    /// （与歌词来源列表是构造函数注入、必须重启形成对照）。
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
            log?.Invoke($"[server] 使用歌词服务器: {endpoint!.Trim()}");
        }
        catch (Exception e)
        {
            // 地址格式错误等 —— 在没有服务器的情况下继续工作（这是功能降级而非错误）。
            coordinator.RemoteCache = null;
            log?.Invoke($"[server] 地址无效，因此不使用: {e.Message}");
        }
    }

    /// <summary>
    /// （重新）应用协调器的翻译配置 —— 引擎/密钥/目标语言变更时调用（保存设置等）。
    /// 把翻译器失败同时路由到协调器（翻译显示状态）和 App（日志、提示），
    /// 并立即重新发布当前行，让新的翻译/状态马上反映出来。
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
