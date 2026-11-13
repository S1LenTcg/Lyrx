namespace Musebase.Core.Meaning;

/// <summary>
/// 체인에 거는 훅. <paramref name="Skip"/>은 지금 부르지 않을 엔진(한도·회로 차단),
/// <paramref name="OnResult"/>는 엔진마다 결과를 받는다(미터·차단 기록).
/// </summary>
public sealed record MeaningChainHooks(
    Func<string, bool>? Skip = null,
    Action<string, MeaningWriteResult>? OnResult = null);

/// <summary>
/// 의미 엔진 체인 — 앞 엔진이 문단을 못 쓰면 다음 엔진에 맡긴다(번역의 CompositeTranslator와 같은 역할).
///
/// "무료인 동안 Gemini, 모자라면 OpenRouter"가 목적이다. Gemini는 잔액이 떨어지면 402, 무료 티어
/// 한도면 429를 준다 — 둘 다 곡의 문제가 아니므로 다음 엔진이 받는다. <b>빈 응답(<see cref="MeaningWriteResult.Failed"/>)도
/// 넘긴다</b> — 모델이 생각에 출력 상한을 다 써 버리는 등 엔진 사정인 경우가 많고, 한 번 더 부르는 값은
/// 곡을 "실패"로 굳히는 값보다 싸다. 문단이 나오면(자료부족 답 포함) 거기서 끝이다.
///
/// 모든 엔진이 실패하면 마지막 결과를, 전부 건너뛰었으면 "잠시 후 다시"를 돌려준다(저장되지 않는다).
/// </summary>
public sealed class CompositeMeaningWriter : IMeaningWriter
{
    private readonly IReadOnlyList<IMeaningWriter> _members;
    private readonly MeaningChainHooks _hooks;

    public CompositeMeaningWriter(IReadOnlyList<IMeaningWriter> members, MeaningChainHooks? hooks = null)
    {
        if (members.Count == 0) throw new ArgumentException("엔진이 하나는 있어야 합니다.", nameof(members));
        _members = members;
        _hooks = hooks ?? new MeaningChainHooks();
    }

    /// <summary>대표 id·모델은 주 엔진의 것이다. 실제로 쓴 엔진은 결과의 <see cref="MeaningWriteResult.Engine"/>에 있다.</summary>
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

        return last ?? MeaningWriteResult.Transient with { Reason = "모든 엔진이 쉬는 중입니다(한도·차단)" };
    }
}
