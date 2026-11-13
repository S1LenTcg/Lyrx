namespace Musebase.Core.Search;

/// <summary>의미 조회 한 번의 결과 — 글(있으면), 그리고 서버가 지금 만드는 중인가.</summary>
public sealed record MeaningLookup(SongMeaningView? Meaning, bool Pending, int RetryAfterMs)
{
    public static readonly MeaningLookup None = new(null, false, 0);
}

/// <summary>
/// "서버가 만드는 중이면 잠시 기다렸다 받는다" — 가사의 서버 번역 받기와 같은 흐름을 의미에도 쓴다.
///
/// 새로 올라온 곡은 서버가 의미를 자동으로 만든다(수 초). 앱이 그 사이에 물으면 404에 <c>pending</c>이
/// 오고, 여기서 <c>retryAfterMs</c> 간격으로 다시 묻는다. 서버가 힌트를 주지 않으면(없음·자료없음으로
/// 끝난 곡·구버전 서버) 한 번 묻고 끝난다 — 기다려도 소용없는 곡에 요청을 쌓지 않는다.
///
/// <b>await에 ConfigureAwait(false)를 쓰지 않는다</b> — <paramref name="onPending"/>이 화면을 고치므로
/// UI 스레드에서 불러야 한다(WPF·Android 모두).
/// </summary>
public static class MeaningFetch
{
    /// <summary>기다리는 상한(ms). 서버 생성은 보통 2~5초라 넉넉하다.</summary>
    public const int DefaultMaxWaitMs = 20_000;

    public static async Task<SongMeaningView?> AwaitAsync(
        IRemoteLyricsCache remote, string title, string artist,
        Action? onPending = null, int maxWaitMs = DefaultMaxWaitMs, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        var announced = false;
        while (true)
        {
            var got = await remote.LookupMeaningAsync(title, artist, ct);
            if (got.Meaning is not null || !got.Pending || ct.IsCancellationRequested) return got.Meaning;

            if (!announced)
            {
                announced = true;
                onPending?.Invoke();
            }

            var wait = Math.Clamp(got.RetryAfterMs, 1000, 5000);
            if (DateTimeOffset.UtcNow.AddMilliseconds(wait) > deadline) return null;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return null; }
        }
    }
}
