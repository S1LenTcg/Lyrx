namespace Musebase.Core.Meaning;

/// <summary>
/// 挂在链上的钩子。<paramref name="Skip"/> 表示当前不该调用的引擎(额度、熔断)，
/// <paramref name="OnResult"/> 接收每个引擎的结果(计量、熔断记录)。
/// </summary>
public sealed record MeaningChainHooks(
    Func<string, bool>? Skip = null,
    Action<string, MeaningWriteResult>? OnResult = null);

/// <summary>
/// 含义引擎链 —— 前一个引擎写不出段落就交给下一个(角色同翻译的 CompositeTranslator)。
///
/// 目标是"免费期间用 Gemini，不够再上 OpenRouter"。Gemini 余额耗尽会返回 402，免费额度
/// 用完会返回 429 —— 两者都不是歌曲本身的问题，因此由下一个引擎接手。<b>空响应(<see cref="MeaningWriteResult.Failed"/>)也
/// 照传</b> —— 很多时候是模型把输出上限都花在思考上这类引擎侧原因，多调用一次
/// 也比把这首歌固化成"失败"更划算。一旦产出段落(包括资料不足的答复)就到此为止。
///
/// 所有引擎都失败则返回最后一个结果，全部被跳过则返回"请稍后重试"(不会被保存)。
/// </summary>
public sealed class CompositeMeaningWriter : IMeaningWriter
{
    private readonly IReadOnlyList<IMeaningWriter> _members;
    private readonly MeaningChainHooks _hooks;

    public CompositeMeaningWriter(IReadOnlyList<IMeaningWriter> members, MeaningChainHooks? hooks = null)
    {
        if (members.Count == 0) throw new ArgumentException("至少需要一个引擎。", nameof(members));
        _members = members;
        _hooks = hooks ?? new MeaningChainHooks();
    }

    /// <summary>代表 id 与模型来自主引擎。实际使用的引擎在结果的 <see cref="MeaningWriteResult.Engine"/> 里。</summary>
    public string EngineId => _members[0].EngineId;

    public string Model => _members[0].Model;

    public IReadOnlyList<IMeaningWriter> Members => _members;

    public async Task<MeaningWriteResult> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default)
    {
        MeaningWriteResult? last = null;
        foreach (var member in _members)
        {
            if (_hooks.Skip?.Invoke(member.EngineId) == true) continue;

            var result = await member.WriteAsync(title, artist, sources, targetLang, ct).ConfigureAwait(false);
            result = result with { Engine = member.EngineId, Model = member.Model };
            _hooks.OnResult?.Invoke(member.EngineId, result);

            if (result.Text is not null) return result;
            last = result;
        }

        return last ?? MeaningWriteResult.Transient with { Reason = "所有引擎都在休息中(额度·熔断)" };
    }
}
