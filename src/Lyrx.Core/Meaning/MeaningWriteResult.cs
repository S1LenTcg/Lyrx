using System.Net;
using System.Text.Json;

namespace Musebase.Core.Meaning;

/// <summary>
/// 의미 생성 한 번의 결과.
///
/// 성공/실패 두 갈래로는 부족해서 실패를 셋으로 나눈다. 갈래마다 <b>저장할지</b>와
/// <b>사람에게 무엇을 하라고 할지</b>가 다르기 때문이다.
///
/// | 갈래 | 예 | 저장 | 사람이 할 일 |
/// |---|---|---|---|
/// | <see cref="Retryable"/> | 429, 5xx, 타임아웃 | 안 함 | 잠시 후 다시 |
/// | <see cref="NeedsSetup"/> | 402 잔액, 401/403 키, 404 모델 | 안 함 | 결제·키·모델을 고친다 |
/// | 그 밖(<see cref="Failed"/>) | 200인데 본문이 빔 | 함 | 없음(그 곡의 문제) |
///
/// **엔진 설정 문제를 저장하면 안 된다.** 백필은 행이 있는 곡을 건너뛰므로, 잔액이 떨어진 채
/// 돌리면 남은 곡이 전부 "실패"로 박제된다. 반대로 "잠시 후 다시"라고만 하면 사람은 저절로 안
/// 풀리는 문제를 기다린다 — 실제로 402(잔액)와 404(무료 모델 정책)가 그렇게 숨었다.
/// 그래서 공급자가 준 이유(<see cref="Reason"/>)를 버리지 않고 끝까지 싣는다.
/// </summary>
/// <param name="Text">생성된 문단. null이면 실패다.</param>
/// <param name="Retryable">시간이 지나면 풀릴 실패인가 — 그렇다면 저장하지 않는다.</param>
/// <param name="NeedsSetup">엔진 설정(결제·키·모델)을 사람이 고쳐야 하는 실패 — 저장하지 않는다.</param>
/// <param name="StatusCode">공급자가 준 HTTP 상태. 응답을 못 받았으면 null.</param>
/// <param name="Reason">공급자가 준 오류 문장(짧게 자른 것). 로그와 관리 화면에 그대로 보인다.</param>
public sealed record MeaningWriteResult(
    string? Text,
    bool Retryable,
    bool NeedsSetup = false,
    int? StatusCode = null,
    string? Reason = null)
{
    /// <summary>
    /// 실제로 쓴 엔진·모델. 체인(<see cref="CompositeMeaningWriter"/>)에서는 앞 엔진이 실패하고
    /// 뒤 엔진이 쓸 수 있어서, 라이터의 대표 id로는 누가 썼는지 알 수 없다. 비면 라이터의 값을 쓴다.
    /// </summary>
    public string? Engine { get; init; }

    /// <inheritdoc cref="Engine"/>
    public string? Model { get; init; }

    /// <summary>사유 문장은 이 길이에서 자른다 — 로그 한 줄·화면 한 줄에 들어가야 한다.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>영구 실패(응답이 비었다 등 그 곡의 문제) — 저장해 두고 넘어간다.</summary>
    public static readonly MeaningWriteResult Failed = new(null, false);

    /// <summary>일시적 실패(쿼타·서버·네트워크·타임아웃) — 저장하지 않는다.</summary>
    public static readonly MeaningWriteResult Transient = new(null, true);

    public static MeaningWriteResult Written(string text) => new(text, false);

    /// <summary>키가 비어 있다 — 부를 것도 없이 설정 문제다.</summary>
    public static MeaningWriteResult MissingKey() => new(null, false, NeedsSetup: true, Reason: "API 키가 비어 있습니다");

    /// <summary>
    /// 응답을 받았지만 실패한 경우. 기준은 "다시 부르면 달라지나"와 "곡 탓인가"다.
    /// - 429·5xx: 기다리면 풀린다 → <see cref="Retryable"/>.
    /// - 그 밖의 4xx: 같은 설정으로는 어느 곡을 불러도 같다 → <see cref="NeedsSetup"/>.
    ///   402(잔액)는 충전해야, 401/403은 키를, 404는 모델 이름·계정 정책을 고쳐야 풀린다.
    ///   400도 여기다 — 잘못된 모델 id나 키 형식이 400으로 온다(Gemini `API_KEY_INVALID`).
    /// </summary>
    public static MeaningWriteResult FromStatus(HttpStatusCode code, string? body = null)
    {
        var status = (int)code;
        var reason = ReasonOf(body);
        return code == HttpStatusCode.TooManyRequests || status >= 500
            ? new(null, true, StatusCode: status, Reason: reason)
            : new(null, false, NeedsSetup: status is >= 400 and < 500, StatusCode: status, Reason: reason);
    }

    /// <summary>응답 본문에서 사람이 읽을 한 문장을 뽑는다. OpenRouter·Gemini 모두 <c>{"error":{"message"}}</c>다.</summary>
    public static string? ReasonOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = body!;
        try
        {
            using var doc = JsonDocument.Parse(body!);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    text = message.GetString() ?? body!;
                else if (error.ValueKind == JsonValueKind.String)
                    text = error.GetString() ?? body!;
            }
        }
        catch (JsonException)
        {
            // JSON이 아니면 본문을 그대로 쓴다(프록시 오류 페이지 등).
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= MaxReasonLength ? oneLine : oneLine[..MaxReasonLength] + "…";
    }
}
