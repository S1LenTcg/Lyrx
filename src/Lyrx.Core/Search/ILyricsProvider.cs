using System.Net;
using System.Runtime.CompilerServices;

namespace Musebase.Core.Search;

/// <summary>가사 제공자 공통 인터페이스. LyricsKit의 LyricsProvider 포팅.</summary>
public interface ILyricsProvider
{
    string ServiceName { get; }

    /// <summary>검색 → 후보별 가사 취득. 도착하는 대로 스트리밍.</summary>
    IAsyncEnumerable<Lyrics> GetLyricsAsync(LyricsSearchRequest request, CancellationToken ct = default);
}

/// <summary>
/// 토큰(검색 결과 항목) 기반 제공자 베이스. LyricsKit의 _LyricsProvider 포팅.
/// 검색 실패는 빈 스트림, 개별 후보 취득 실패는 해당 항목만 건너뛴다.
/// </summary>
public abstract class LyricsProviderBase<TToken> : ILyricsProvider
{
    public abstract string ServiceName { get; }

    protected abstract Task<IReadOnlyList<TToken>> SearchAsync(LyricsSearchRequest request, CancellationToken ct);

    protected abstract Task<Lyrics?> FetchAsync(TToken token, CancellationToken ct);

    public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
        LyricsSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<TToken> tokens;
        try
        {
            tokens = await SearchAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch
        {
            // 제공자 하나의 검색 실패가 전체 검색을 막지 않도록 조용히 종료
            yield break;
        }

        // 후보 병렬 취득, 검색 순서대로 산출
        var fetchTasks = tokens.Take(request.Limit)
            .Select(t => FetchAsync(t, ct))
            .ToList();

        foreach (var task in fetchTasks)
        {
            Lyrics? lyrics = null;
            try
            {
                lyrics = await task.ConfigureAwait(false);
            }
            catch
            {
                // 개별 후보 실패는 건너뜀
            }
            if (lyrics is null) continue;

            lyrics.Metadata.ServiceName = ServiceName;
            lyrics.Metadata.Request = request;
            yield return lyrics;
        }
    }
}

/// <summary>제공자 공용 HttpClient (커넥션 재사용)</summary>
public static class LyricsHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false, // 쿠키는 제공자별로 수동 관리 (NetEase 2-pass)
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
