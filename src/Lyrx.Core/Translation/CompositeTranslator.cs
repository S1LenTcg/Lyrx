using System.Net;
using System.Net.Http;

namespace Musebase.Core.Translation;

/// <summary>翻译器失败信息（用于日志、状态提示）。不包含个人信息与歌词正文。</summary>
public sealed record TranslatorFailure(string EngineId, int? HttpStatus, TranslatorFailureKind Kind);

/// <summary>失败分类 —— App 把它本地化为用户提示文案。</summary>
public enum TranslatorFailureKind
{
    Quota,    // 配额超限 (DeepL 456)
    Auth,     // 认证失败 / 被拒 (401/403)
    RateLimit,// 请求过于频繁 (429)
    Server,   // 服务器错误 (5xx)
    Network,  // 连接失败等
    Other,
}

/// <summary>
/// 走链期间外部可以插入的挂点。全部可选，不提供时行为与以前相同。
///
/// <b>为什么用回调。</b> "跳过这个引擎"这一判断的依据（额度要关闭到什么时候）在
/// 每个调用方都不同 —— 服务器的批量任务知道"在这个作业期间"这样的生命周期，而应用以歌曲为单位
/// 存活很短，没有这个概念。反过来，<b>遍历本身</b>只能在这里做。所以策略放在外面，
/// 机制放在里面。
/// </summary>
/// <param name="OnFailure">引擎抛出异常时（取消除外）。用于日志、状态提示。</param>
/// <param name="Skip">
/// 为真时干脆不调用那个引擎。用于避免对已经撞到额度的引擎每首歌再去撞一次。
/// </param>
/// <param name="OnFilled">
/// 一个引擎实际填充的行数与字符数。<b>各引擎的贡献只在这个循环内可知</b> ——
/// <see cref="TranslateAsync"/> 只返回扁平的字符串数组，外部无从推导。
/// 用量统计（按字符计费引擎的软计量）与任务汇总就挂在这一个值上。
/// </param>
public sealed record CompositeTranslatorHooks(
    Action<TranslatorFailure>? OnFailure = null,
    Func<string, bool>? Skip = null,
    Action<string, int, int>? OnFilled = null);

/// <summary>
/// 按顺序尝试多个翻译器的回退链（ADR-0002）。
/// 前面的翻译器失败（例如 DeepL 配额 456）时，只把剩下的条目交给下一个翻译器填充。
/// 单个翻译器的失败通过 <see cref="CompositeTranslatorHooks.OnFailure"/> 上报，但不作为异常
/// 向外传播（取消除外）。即使只包装一个翻译器，也会产生失败上报路径。
/// </summary>
public sealed class CompositeTranslator : ITranslator
{
    private readonly IReadOnlyList<(string EngineId, ITranslator Translator)> _chain;
    private readonly CompositeTranslatorHooks _hooks;

    public CompositeTranslator(
        IReadOnlyList<(string EngineId, ITranslator Translator)> chain,
        Action<TranslatorFailure>? onFailure = null)
        : this(chain, new CompositeTranslatorHooks(onFailure))
    {
    }

    public CompositeTranslator(
        IReadOnlyList<(string EngineId, ITranslator Translator)> chain,
        CompositeTranslatorHooks? hooks)
    {
        _chain = chain;
        _hooks = hooks ?? new CompositeTranslatorHooks();
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        var pendingIndices = Enumerable.Range(0, texts.Count).ToList();

        foreach (var (engineId, translator) in _chain)
        {
            if (pendingIndices.Count == 0) break;

            // 已经知道被卡住的引擎就不调用 —— 对撞到额度的引擎每首歌再撞一次，只会
            // 增加空请求，还会持续捶打那个引擎的额度。
            if (_hooks.Skip?.Invoke(engineId) == true) continue;

            var pendingTexts = pendingIndices.Select(i => texts[i]).ToList();
            IReadOnlyList<string?> partial;
            try
            {
                partial = await translator.TranslateAsync(pendingTexts, targetLang, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _hooks.OnFailure?.Invoke(new TranslatorFailure(engineId, HttpStatusOf(ex), Classify(ex)));
                continue; // 回退到下一个翻译器
            }

            var stillPending = new List<int>();
            var filledLines = 0;
            var filledChars = 0;
            for (var i = 0; i < pendingIndices.Count; i++)
            {
                var value = i < partial.Count ? partial[i] : null;
                if (value is { Length: > 0 })
                {
                    results[pendingIndices[i]] = value;
                    filledLines++;
                    // 统计发送侧（原文）的长度 —— 按字符计费以原文为基准，这样用量估算与
                    // 计费才会是同一单位。
                    filledChars += texts[pendingIndices[i]].Length;
                }
                else
                {
                    stillPending.Add(pendingIndices[i]);
                }
            }
            if (filledLines > 0) _hooks.OnFilled?.Invoke(engineId, filledLines, filledChars);
            pendingIndices = stillPending;
        }

        return results;
    }

    private static int? HttpStatusOf(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code } ? (int)code : null;

    private static TranslatorFailureKind Classify(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            var status = (int?)hre.StatusCode;
            return status switch
            {
                456 => TranslatorFailureKind.Quota,     // DeepL：配额超限
                401 or 403 => TranslatorFailureKind.Auth,
                429 => TranslatorFailureKind.RateLimit,
                >= 500 and <= 599 => TranslatorFailureKind.Server,
                null => TranslatorFailureKind.Network,   // 连接失败（无状态码）
                _ => TranslatorFailureKind.Other,
            };
        }
        return TranslatorFailureKind.Network;
    }
}
