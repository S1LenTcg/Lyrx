using System.Net;
using System.Net.Http;

namespace Musebase.Core.Translation;

/// <summary>번역기 실패 정보(로깅·상태 힌트용). 개인정보·가사 본문은 담지 않는다.</summary>
public sealed record TranslatorFailure(string EngineId, int? HttpStatus, TranslatorFailureKind Kind);

/// <summary>실패 분류 — App이 사용자 안내 문구로 현지화한다.</summary>
public enum TranslatorFailureKind
{
    Quota,    // 할당량 초과 (DeepL 456)
    Auth,     // 인증 실패/거부 (401/403)
    RateLimit,// 요청 과다 (429)
    Server,   // 서버 오류 (5xx)
    Network,  // 연결 실패 등
    Other,
}

/// <summary>
/// 체인을 도는 동안 바깥이 끼어들 수 있는 자리. 전부 선택이고, 주지 않으면 동작이 이전과 같다.
///
/// <b>왜 콜백인가.</b> "이 엔진을 건너뛴다"는 판단의 근거(한도를 언제까지 닫아 둘 것인가)는
/// 호출자마다 다르다 — 서버의 일괄 작업은 "이 잡 동안"이라는 수명을 알지만, 앱은 곡 단위로 짧게
/// 살아 그런 개념이 없다. 반대로 <b>순회 자체</b>는 여기서만 할 수 있다. 그래서 정책은 바깥에,
/// 기전은 안에 둔다.
/// </summary>
/// <param name="OnFailure">엔진이 예외를 던졌을 때(취소 제외). 로깅·상태 힌트용.</param>
/// <param name="Skip">
/// 참이면 그 엔진을 아예 부르지 않는다. 한 번 한도를 맞은 엔진에 곡마다 다시 부딪히지 않게 하는 용도.
/// </param>
/// <param name="OnFilled">
/// 한 엔진이 실제로 채운 줄 수와 문자 수. <b>엔진별 기여는 이 루프 안에서만 알 수 있다</b> —
/// <see cref="TranslateAsync"/>는 평평한 문자열 배열만 돌려주므로 바깥에서는 유도할 방법이 없다.
/// 사용량 집계(문자 과금 엔진의 소프트 미터)와 작업 요약이 이 값 하나에 걸려 있다.
/// </param>
public sealed record CompositeTranslatorHooks(
    Action<TranslatorFailure>? OnFailure = null,
    Func<string, bool>? Skip = null,
    Action<string, int, int>? OnFilled = null);

/// <summary>
/// 여러 번역기를 순서대로 시도하는 폴백 체인(ADR-0002).
/// 앞 번역기가 실패(예: DeepL 할당량 456)하면 남은 항목만 다음 번역기로 채운다.
/// 개별 번역기 실패는 <see cref="CompositeTranslatorHooks.OnFailure"/>로 보고하되 예외로
/// 전파하지 않는다(취소는 예외). 단일 번역기를 감싸도 실패 보고 경로가 생긴다.
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

            // 이미 막힌 것을 아는 엔진은 부르지 않는다 — 한도를 맞은 엔진에 곡마다 다시 부딪히면
            // 헛요청만 늘고 그 엔진의 한도를 계속 두드리게 된다.
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
                continue; // 다음 번역기로 폴백
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
                    // 보낸 쪽(원문) 길이를 센다 — 문자 과금이 원문 기준이고, 그래야 사용량 추정과
                    // 청구가 같은 단위가 된다.
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
                456 => TranslatorFailureKind.Quota,     // DeepL: 할당량 초과
                401 or 403 => TranslatorFailureKind.Auth,
                429 => TranslatorFailureKind.RateLimit,
                >= 500 and <= 599 => TranslatorFailureKind.Server,
                null => TranslatorFailureKind.Network,   // 연결 실패(상태 코드 없음)
                _ => TranslatorFailureKind.Other,
            };
        }
        return TranslatorFailureKind.Network;
    }
}
