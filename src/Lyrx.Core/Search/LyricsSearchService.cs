using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Musebase.Core.Search;

/// <summary>
/// 다중 제공자 병렬 검색 집계. LyricsKit의 Provider/Group.swift에 해당.
/// 모든 제공자를 동시에 돌리고 결과를 도착 순으로 스트리밍하거나,
/// 품질 순으로 정렬해 반환한다.
/// </summary>
public sealed class LyricsSearchService
{
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    public LyricsSearchService(params ILyricsProvider[] providers)
    {
        _providers = providers.Length > 0
            ? providers
            : [new LrclibProvider(), new NetEaseProvider(), new KugouProvider(), new QQMusicProvider()];
    }

    /// <summary>
    /// 모든 제공자 결과를 도착 순으로 스트리밍 (제공자 간 순서 비보장).
    /// 원본 검색어와 함께 정제 변형(피처링/리마스터 등 제거)도 검색해 커버리지를 넓히고,
    /// (제공자, 곡 토큰) 기준으로 중복 결과를 제거한다.
    /// <paramref name="diagnostics"/>를 주면 소스별 히트·지연을 집계한다(텔레메트리용, 곡 정보 없음).
    /// </summary>
    public async IAsyncEnumerable<Lyrics> SearchAsync(
        LyricsSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default,
        SearchDiagnostics? diagnostics = null)
    {
        var requests = new List<LyricsSearchRequest> { request };
        foreach (var variant in SearchTermCleaner.Variants(request.Term))
            requests.Add(request with { Term = variant });

        var channel = Channel.CreateUnbounded<Lyrics>();
        var seen = new HashSet<string>();
        var seenLock = new object();

        async Task ProcessAsync(ILyricsProvider provider, LyricsSearchRequest req)
        {
            var sw = diagnostics is null ? null : Stopwatch.StartNew();
            var hit = false;
            try
            {
                await foreach (var lyrics in provider.GetLyricsAsync(req, ct).ConfigureAwait(false))
                {
                    if (!hit)
                    {
                        hit = true; // 중복 제거 전 판정 — 제공자가 결과를 냈다는 사실 자체를 기록
                        diagnostics?.ReportHit(provider.ServiceName, sw!.ElapsedMilliseconds);
                    }
                    bool fresh;
                    lock (seenLock) fresh = seen.Add(DedupKey(lyrics));
                    if (fresh) await channel.Writer.WriteAsync(lyrics, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 취소는 조용히
            }
            finally
            {
                if (!hit) diagnostics?.ReportMiss(provider.ServiceName, sw?.ElapsedMilliseconds ?? 0);
            }
        }

        var workers = (from provider in _providers
                       from req in requests
                       select ProcessAsync(provider, req)).ToArray();

        _ = Task.WhenAll(workers).ContinueWith(
            _ => channel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var lyrics in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return lyrics;
    }

    /// <summary>중복 판정 키: 같은 제공자의 같은 곡(토큰)이면 동일. 토큰이 없으면 제목+첫 줄로 대체.</summary>
    private static string DedupKey(Lyrics lyrics)
    {
        var service = lyrics.Metadata.ServiceName ?? "?";
        if (!string.IsNullOrEmpty(lyrics.Metadata.ServiceToken))
            return $"{service}|{lyrics.Metadata.ServiceToken}";

        var title = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle) ?? "";
        var first = lyrics.Lines.Count > 0 ? lyrics.Lines[0].Content : "";
        return $"{service}|{title}|{first}";
    }

    /// <summary>전체 결과를 모아 품질 내림차순으로 반환</summary>
    public async Task<List<Lyrics>> SearchAllAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var results = new List<Lyrics>();
        await foreach (var lyrics in SearchAsync(request, ct).ConfigureAwait(false))
            results.Add(lyrics);
        return results.OrderByDescending(l => l.Quality()).ToList();
    }

    /// <summary>최고 품질 후보 1건 (없으면 null)</summary>
    public async Task<Lyrics?> SearchBestAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var all = await SearchAllAsync(request, ct).ConfigureAwait(false);
        return all.FirstOrDefault();
    }
}
