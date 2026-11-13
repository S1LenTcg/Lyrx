namespace Musebase.Core.Meaning;

/// <summary>한 곡에 대한 의미 생성 결과.</summary>
/// <param name="Status">`ok` | `no-source` | `insufficient` | `failed` | `retry` | `config`.</param>
/// <param name="Summary">생성된 대상 언어 문단. `ok`가 아니면 null.</param>
/// <param name="Sources">근거로 쓴 원문들(출처 표기·재생성 판단용).</param>
/// <param name="Detail">만들지 못했을 때 공급자가 준 이유(상태코드 포함). 로그·관리 화면용.</param>
public sealed record SongMeaning(
    string Status,
    string? Summary,
    IReadOnlyList<MeaningSource> Sources,
    string? Engine,
    string? Model,
    string? Detail = null)
{
    public const string Ok = "ok";
    /// <summary>어느 소스에도 자료가 없었다 — LLM은 부르지 않았다.</summary>
    public const string NoSource = "no-source";
    /// <summary>자료는 있었지만 생성이 영구적으로 실패했다(키가 틀렸다, 응답이 비었다).</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 일시적 실패(쿼타·서버·네트워크) — **저장하지 않는다.** 저장하면 쿼타가 풀린 뒤에도
    /// 백필이 이 곡을 영영 건너뛴다. 이 상태는 DB에 들어가지 않는 값이다.
    /// </summary>
    public const string Retry = "retry";

    /// <summary>
    /// 엔진 설정(결제 잔액·키·모델 이름·계정 정책)을 사람이 고쳐야 한다 — **저장하지 않는다.**
    /// 곡의 문제가 아니므로 행으로 남기면 백필이 멀쩡한 곡을 영영 건너뛴다. `retry`와 달리
    /// 기다려도 풀리지 않으므로 화면에 이유를 그대로 보여 줘야 한다. DB에 들어가지 않는 값이다.
    /// </summary>
    public const string Config = "config";

    /// <summary>저장하지 않는 상태인가(<see cref="Retry"/>·<see cref="Config"/>).</summary>
    public static bool IsUnsaved(string status) => status is Retry or Config;

    /// <summary>
    /// 자료는 찾았지만 그것만으로는 곡의 의미를 말할 수 없었다. 문단은 남기되(사람이 판단할 수
    /// 있게) **"의미 있음"으로 세지 않는다** — 글자가 있다는 이유로 세면 통계가 부풀고,
    /// 앱에는 "파악하기 어렵다"는 문장이 곡 해설이라며 뜬다.
    /// </summary>
    public const string Insufficient = "insufficient";

    public string? GeniusUrl =>
        Sources.FirstOrDefault(s => s.Name == "Genius")?.Url;
}

/// <summary>
/// 소스 수집 → 요약을 한 번에 수행한다.
///
/// 두 가지가 설계의 핵심이다.
/// 1. **소스는 병렬로, 실패는 무시.** 하나가 죽어도 나머지가 채운다.
/// 2. **자료가 하나도 없으면 LLM을 부르지 않는다.** 곡 해설은 그럴듯한 창작이 특히 쉬운
///    영역이라, 근거 없이 부르면 모델이 지어낸다. 토큰 낭비이기도 하다.
/// </summary>
public sealed class SongMeaningService
{
    private readonly IReadOnlyList<ISongMeaningSource> _sources;
    private readonly IMeaningWriter? _writer;

    public SongMeaningService(IReadOnlyList<ISongMeaningSource> sources, IMeaningWriter? writer)
    {
        _sources = sources;
        _writer = writer;
    }

    /// <summary>소스도 엔진도 구성되지 않았으면 이 기능은 꺼진 것이다.</summary>
    public bool IsEnabled => _writer is not null && _sources.Count > 0;

    /// <summary>켜져 있는 소스 이름들 — 무엇에 근거해 만들어지는지 화면에 보여 주기 위한 것.</summary>
    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public async Task<SongMeaning> BuildAsync(
        string title, string artist, string targetLang, CancellationToken ct = default)
    {
        var collected = await CollectAsync(title, artist, ct).ConfigureAwait(false);
        if (collected.Count == 0)
            return new SongMeaning(SongMeaning.NoSource, null, collected, _writer?.EngineId, _writer?.Model);

        if (_writer is null)
            return new SongMeaning(SongMeaning.Failed, null, collected, null, null);

        var written = await _writer.WriteAsync(title, artist, collected, targetLang, ct).ConfigureAwait(false);
        if (written.Text is not null)
        {
            // "자료가 부족하다"는 답도 정상 동작이지만 의미는 아니다 — 갈라서 기록한다.
            var verdict = MeaningVerdict.IsInsufficient(written.Text)
                ? SongMeaning.Insufficient
                : SongMeaning.Ok;
            return new SongMeaning(
                verdict, MeaningVerdict.Strip(written.Text), collected,
                written.Engine ?? _writer.EngineId, written.Model ?? _writer.Model);
        }

        // 쿼타·네트워크처럼 시간이 풀어 줄 실패와 설정 문제는 `failed`로 굳히지 않는다.
        var status = written.NeedsSetup ? SongMeaning.Config
            : written.Retryable ? SongMeaning.Retry
            : SongMeaning.Failed;
        return new SongMeaning(status, null, collected,
            written.Engine ?? _writer.EngineId, written.Model ?? _writer.Model, DetailOf(written));
    }

    /// <summary>"HTTP 402 · 잔액이 부족합니다"처럼 상태코드와 이유를 한 줄로.</summary>
    private static string? DetailOf(MeaningWriteResult written) =>
        (written.StatusCode, written.Reason) switch
        {
            ({ } code, { } reason) => $"HTTP {code} · {reason}",
            ({ } code, null) => $"HTTP {code}",
            (null, { } reason) => reason,
            _ => null,
        };

    /// <summary>모든 소스를 동시에 부르고 성공한 것만 모은다(레지스트리 등록 순서 유지).</summary>
    public async Task<IReadOnlyList<MeaningSource>> CollectAsync(
        string title, string artist, CancellationToken ct = default)
    {
        var tasks = _sources.Select(s => s.FetchAsync(title, artist, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }
}
