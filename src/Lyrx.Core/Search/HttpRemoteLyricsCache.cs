using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Search;

/// <summary>
/// <see cref="IRemoteLyricsCache"/>의 HTTP 구현 — 개인 가사 서버(`contracts/lyrics-api.md` v1).
///
/// 설계 원칙 세 가지:
/// 1. **조용한 강등** — 모든 예외를 삼키고 조회는 null, 저장은 무시. 서버가 죽어도 앱은 그대로 동작한다.
/// 2. **짧은 타임아웃** — 조회는 기본 2.5초. 실패해도 그 뒤에 제공자 검색이 이어지므로
///    여기서 오래 끌면 "서버를 켰더니 느려졌다"가 된다.
/// 3. **서킷 브레이커** — 연속 실패가 쌓이면 일정 시간 아예 시도하지 않는다.
///    테일넷 밖(Tailscale 꺼짐)에서 곡이 바뀔 때마다 타임아웃을 기다리는 낭비를 막는다.
/// </summary>
public sealed class HttpRemoteLyricsCache : IRemoteLyricsCache
{
    /// <summary>연속 실패가 이 횟수에 이르면 회로를 연다.</summary>
    private const int FailureThreshold = 2;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(60);
    /// <summary>업로드는 백그라운드라 조회보다 여유를 준다.</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 소켓 고갈을 피하기 위해 인스턴스마다 하나씩 유지한다(설정 변경 시 인스턴스가 새로 만들어진다).
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public HttpRemoteLyricsCache(string endpoint, string? token, int timeoutMs = 2500, Action<string>? log = null)
        : this(endpoint, token, timeoutMs, log, handler: null) { }

    /// <summary>테스트에서 전송 계층을 갈아끼우기 위한 생성자(스텁 핸들러 주입).</summary>
    internal HttpRemoteLyricsCache(
        string endpoint, string? token, int timeoutMs, Action<string>? log, HttpMessageHandler? handler)
    {
        var normalized = endpoint.TrimEnd('/');
        _baseUri = new Uri(normalized + "/", UriKind.Absolute);
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
        _log = log;

        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1.5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // 실제 만료는 요청마다 링크된 CTS로 제어한다(조회/업로드 시간이 다르다).
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
    }

    public async Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return RemoteLyricsResult.Miss;

        try
        {
            var url = new Uri(_baseUri, $"v1/lyrics?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                OnSuccess(); // 미스도 정상 응답이다
                return await ReadMissAsync(response, cts.Token).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode)
            {
                OnFailure($"HTTP {(int)response.StatusCode}");
                return RemoteLyricsResult.Miss;
            }

            var entry = await response.Content.ReadFromJsonAsync<RemoteLyricsEntry>(Json, cts.Token).ConfigureAwait(false);
            OnSuccess();
            if (entry?.Lrc is not { Length: > 0 } lrc) return RemoteLyricsResult.Miss;

            var lyrics = Lyrics.Parse(lrc);
            if (lyrics is null) return RemoteLyricsResult.Miss;
            lyrics.Metadata.ServiceName = entry.Service ?? "Server";
            var langs = entry.Langs ?? [];
            _log?.Invoke($"[server] 히트: {title} — {entry.Service} (match={entry.Match}, langs={string.Join(",", langs)})");
            return new RemoteLyricsResult(lyrics, false, 0, langs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RemoteLyricsResult.Miss; // 트랙 교체 등 — 실패로 세지 않는다
        }
        catch (Exception e)
        {
            OnFailure(e.Message);
            return RemoteLyricsResult.Miss;
        }
    }

    /// <summary>
    /// 404 본문의 양보 힌트를 읽는다. 본문이 없거나 깨졌으면 평범한 미스로 취급한다 —
    /// 구버전 서버(본문 없는 404)와 그대로 호환된다.
    /// </summary>
    private async Task<RemoteLyricsResult> ReadMissAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<MissBody>(Json, ct).ConfigureAwait(false);
            if (body is { Ad: true })
            {
                _log?.Invoke("[server] 광고로 표시된 제목 — 가사를 찾지 않습니다");
                return RemoteLyricsResult.Ad;
            }
            if (body is not { Pending: true }) return RemoteLyricsResult.Miss;
            _log?.Invoke($"[server] 다른 기기도 이 곡을 찾는 중 — 번역을 양보합니다({body.RetryAfterMs}ms)");
            return new RemoteLyricsResult(null, true, body.RetryAfterMs, []);
        }
        catch (Exception)
        {
            return RemoteLyricsResult.Miss;
        }
    }

    /// <summary>
    /// 곡의 의미. 서버에 없으면 404이고 그것은 정상이다 — 대부분의 곡에는 아직 의미가 없다.
    ///
    /// 회로 차단기와 실패 집계를 **가사와 공유한다**: 이건 부가 정보라 여기서 실패했다고
    /// 가사 조회까지 막으면 손해가 크다. 그래서 실패해도 <see cref="OnFailure"/>를 부르지 않고
    /// 조용히 null만 돌려준다(회로가 이미 열려 있으면 아예 시도하지 않는다).
    /// </summary>
    public async Task<SongMeaningView?> GetMeaningAsync(
        string title, string artist, CancellationToken ct = default) =>
        (await LookupMeaningAsync(title, artist, ct).ConfigureAwait(false)).Meaning;

    public async Task<MeaningLookup> LookupMeaningAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return MeaningLookup.None;

        try
        {
            var url = new Uri(_baseUri,
                $"v1/meaning?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = 아직 의미가 없다(정상). 서버가 만드는 중이면 본문에 pending이 온다.
                if (response.StatusCode != HttpStatusCode.NotFound) return MeaningLookup.None;
                try
                {
                    var miss = await response.Content.ReadFromJsonAsync<MissBody>(Json, cts.Token).ConfigureAwait(false);
                    return miss is { Pending: true }
                        ? new MeaningLookup(null, true, miss.RetryAfterMs)
                        : MeaningLookup.None;
                }
                catch (Exception)
                {
                    return MeaningLookup.None;   // 구버전 서버 — 본문 없는 404
                }
            }

            var entry = await response.Content
                .ReadFromJsonAsync<RemoteMeaningEntry>(Json, cts.Token).ConfigureAwait(false);
            return new MeaningLookup(ToView(entry), false, 0);
        }
        catch (Exception)
        {
            return MeaningLookup.None; // 부가 기능 — 가사 조회에 영향을 주지 않는다
        }
    }

    /// <summary>
    /// 의미를 지금 만들어 달라고 요청한다(사람이 버튼을 눌렀을 때만).
    ///
    /// 조회와 달리 <b>회로가 열려 있어도 시도한다</b> — 사람이 방금 누른 동작이라 조용히
    /// 아무 일도 안 일어나면 고장으로 보인다. 대신 결과는 실패로 정직하게 돌려준다.
    /// 타임아웃도 따로 길게 잡는다: 외부 자료 수집 + LLM이라 수십 초가 걸린다.
    /// </summary>
    public async Task<MeaningRequestResult> RequestMeaningAsync(
        string title, string artist, bool force = false, CancellationToken ct = default)
    {
        try
        {
            // force가 없으면 서버가 만들어 둔 것을 그대로 준다 — 기존 글을 덮어쓰지 않는다.
            var url = new Uri(_baseUri,
                $"v1/meaning?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}"
                + (force ? "&force=1" : ""));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GenerateTimeout);

            using var response = await _http.PostAsync(url, null, cts.Token).ConfigureAwait(false);

            // 403(앱 생성 꺼짐) · 503(엔진 미구성) — 둘 다 "이 서버에서는 안 된다"는 같은 말이다.
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                                    or System.Net.HttpStatusCode.ServiceUnavailable)
                return MeaningRequestResult.Of(MeaningRequestStatus.Unavailable);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var notMade = await response.Content
                    .ReadFromJsonAsync<RemoteMeaningNotMade>(Json, cts.Token).ConfigureAwait(false);
                return MeaningRequestResult.Of(notMade?.Status switch
                {
                    "no-source" => MeaningRequestStatus.NoSource,
                    "insufficient" => MeaningRequestStatus.Insufficient,
                    "retry" => MeaningRequestStatus.Retry,
                    // 서버 엔진의 결제·키·모델 문제 — 앱에서 할 수 있는 일은 없고 서버를 고쳐야 한다.
                    "config" => MeaningRequestStatus.Unavailable,
                    _ => MeaningRequestStatus.Failed,
                });
            }

            if (!response.IsSuccessStatusCode) return MeaningRequestResult.Of(MeaningRequestStatus.Failed);

            var entry = await response.Content
                .ReadFromJsonAsync<RemoteMeaningEntry>(Json, cts.Token).ConfigureAwait(false);
            var view = ToView(entry);
            return view is null
                ? MeaningRequestResult.Of(MeaningRequestStatus.Failed)
                : new MeaningRequestResult(MeaningRequestStatus.Created, view);
        }
        catch (Exception)
        {
            return MeaningRequestResult.Of(MeaningRequestStatus.Failed);
        }
    }

    /// <summary>생성은 외부 자료 수집 + LLM이라 조회 타임아웃(수 초)으로는 늘 끊긴다.</summary>
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromSeconds(90);

    // ---- 곡에 딸린 것들(커버·좋아요) ----

    /// <summary>
    /// 서버가 아직 커버를 안 찾아본 곡이면 그쪽에서 외부 API를 한 번 부르므로 가사 조회보다 느리다.
    /// 그래서 <b>가사와 같은 타임아웃을 쓰지 않고</b> 넉넉히 잡되, 실패는 조용히 null이다.
    /// 부가 정보라 회로 차단기에 세지 않는다 — 이것 때문에 가사가 막히면 손해가 크다.
    /// </summary>
    public async Task<SongExtrasResult> GetExtrasAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return SongExtrasResult.NotFound;
        try
        {
            using var response = await SendExtrasAsync(
                HttpMethod.Get, "v1/song", title, artist, null, ct).ConfigureAwait(false);

            // 404는 "서버가 그 곡을 모른다"일 뿐이다 — 아직 아무도 올리지 않은 새 곡이면 정상이고,
            // 몇 초 뒤 이 기기가 올린다. 오류로 올리면 새 곡마다 경고가 뜬다.
            if (response.StatusCode == HttpStatusCode.NotFound) return SongExtrasResult.NotFound;
            if (!response.IsSuccessStatusCode) return SongExtrasResult.Failed;

            var extras = await ReadExtrasAsync(response, ct).ConfigureAwait(false);
            return extras is null ? SongExtrasResult.Failed : SongExtrasResult.Ok(extras);
        }
        catch (Exception)
        {
            return SongExtrasResult.Failed;   // 주소·네트워크·타임아웃 — 이건 알려야 한다
        }
    }

    public Task<SongExtras?> RefreshCoverAsync(string title, string artist, CancellationToken ct = default) =>
        ExtrasAsync(HttpMethod.Post, "v1/song/cover", title, artist, null, ct);

    public Task<SongExtras?> SetLovedAsync(
        string title, string artist, bool loved, CancellationToken ct = default) =>
        ExtrasAsync(HttpMethod.Post, "v1/song/love", title, artist, loved ? "1" : "0", ct);

    /// <summary>요청 한 번. 상태 코드를 호출자가 봐야 하므로 응답을 그대로 돌려준다.</summary>
    private async Task<HttpResponseMessage> SendExtrasAsync(
        HttpMethod method, string path, string title, string artist, string? on, CancellationToken ct)
    {
        var query = $"?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist ?? "")}"
                  + (on is null ? "" : $"&on={on}");
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path + query));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExtrasTimeout);

        return await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
    }

    private async Task<SongExtras?> ExtrasAsync(
        HttpMethod method, string path, string title, string artist, string? on, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            using var response = await SendExtrasAsync(method, path, title, artist, on, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await ReadExtrasAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // 부가 정보 — 가사 조회에 영향을 주지 않는다
        }
    }

    /// <summary>성공 응답의 본문을 <see cref="SongExtras"/>로. 본문이 비면 null.</summary>
    private static async Task<SongExtras?> ReadExtrasAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExtrasTimeout);

        var body = await response.Content
            .ReadFromJsonAsync<RemoteSongExtras>(Json, cts.Token).ConfigureAwait(false);

        return body is null
            ? null
            : new SongExtras(
                string.IsNullOrWhiteSpace(body.CoverUrl) ? null : body.CoverUrl,
                body.CoverSource, body.LastFmUrl,
                body.LoveConnected, body.LoveKnown, body.Loved,
                string.IsNullOrWhiteSpace(body.Key) ? null : body.Key,
                body.SpotifyConnected, body.SpotifyKnown, body.SpotifySaved);
    }

    /// <summary>서버가 커버를 처음 찾는 곡이면 외부 API 한 번이 끼어든다(iTunes 2.5초 + 여유).</summary>
    private static readonly TimeSpan ExtrasTimeout = TimeSpan.FromSeconds(12);

    private sealed record RemoteSongExtras
    {
        public string? CoverUrl { get; init; }
        public string? CoverSource { get; init; }
        public string? LastFmUrl { get; init; }
        public bool LoveConnected { get; init; }
        public bool LoveKnown { get; init; }
        public bool Loved { get; init; }

        /// <summary>구버전 서버는 이 필드를 안 보낸다 — 그러면 null이고 관리 화면 버튼만 사라진다.</summary>
        public string? Key { get; init; }

        // 구버전 서버는 안 보낸다 — 전부 false가 되어 Spotify 표시가 그냥 없는 것과 같아진다.
        public bool SpotifyConnected { get; init; }
        public bool SpotifyKnown { get; init; }
        public bool SpotifySaved { get; init; }
    }

    public async Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return;

        try
        {
            var service = lyrics.Metadata.ServiceName;
            var payload = new RemoteLyricsEntry
            {
                Title = title,
                Artist = artist,
                Lrc = lyrics.ToString(),
                Service = service,
                // 사용자 편집본은 서버에서 자동 검색 결과에 덮이지 않도록 보호된다.
                Origin = service == EditedServiceName ? "user" : "provider",
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(UploadTimeout);
            using var response = await _http.PutAsJsonAsync(new Uri(_baseUri, "v1/lyrics"), payload, Json, cts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) OnSuccess();
            else OnFailure($"PUT HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 무시
        }
        catch (Exception e)
        {
            OnFailure(e.Message);
        }
    }

    /// <summary>사용자 편집본 표식 — <c>LyricsCoordinator.SaveEditedLyrics</c>가 넣는 값과 같아야 한다.</summary>
    public const string EditedServiceName = "사용자 편집";

    // ---- 서킷 브레이커 ----

    private bool IsCircuitOpen()
    {
        if (DateTimeOffset.UtcNow >= _openUntil) return false;
        return true;
    }

    private void OnSuccess()
    {
        _consecutiveFailures = 0;
        _openUntil = DateTimeOffset.MinValue;
    }

    private void OnFailure(string reason)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= FailureThreshold)
        {
            _openUntil = DateTimeOffset.UtcNow + OpenDuration;
            _log?.Invoke($"[server] 연속 실패 {_consecutiveFailures}회 — {(int)OpenDuration.TotalSeconds}초간 조회를 건너뜁니다 ({reason})");
        }
        else
        {
            _log?.Invoke($"[server] 실패: {reason}");
        }
    }

    /// <summary>서버 응답/요청 본문(계약 v1, camelCase).</summary>
    private sealed record RemoteLyricsEntry
    {
        public string? Key { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Lrc { get; init; } = "";
        public string? Service { get; init; }
        public string Origin { get; init; } = "provider";
        public string[]? Langs { get; init; }
        public int? LineCount { get; init; }
        public bool? HasInlineTimeTags { get; init; }
        public int? Revision { get; init; }
        public string? UpdatedAt { get; init; }
        public string? Match { get; init; }
    }

    /// <summary>`GET /v1/meaning` 응답(필요한 필드만).</summary>
    /// <summary>
    /// 응답 → 화면에 띄울 형태. 조회와 생성이 <b>같은 모양</b>의 본문을 주므로 한 군데서 옮긴다.
    /// 출처 표기는 의무라 본문과 함께 실어 보낸다(빈 이름은 버린다).
    /// </summary>
    private static SongMeaningView? ToView(RemoteMeaningEntry? entry)
    {
        if (entry?.Summary is not { Length: > 0 } summary) return null;

        var credits = (entry.Attribution ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new MeaningCredit(a.Name!, a.Url))
            .ToList();
        return new SongMeaningView(summary.Trim(), credits, entry.Lang ?? "ko");
    }

    private sealed record RemoteMeaningEntry
    {
        public string? Summary { get; init; }
        public string? Lang { get; init; }
        public RemoteAttribution[]? Attribution { get; init; }
    }

    /// <summary>생성이 202로 끝났을 때의 본문 — 만들지 못한 이유가 들어 있다.</summary>
    private sealed record RemoteMeaningNotMade
    {
        public string? Status { get; init; }
    }

    private sealed record RemoteAttribution
    {
        public string? Name { get; init; }
        public string? Url { get; init; }
    }

    /// <summary>404 본문(계약 v1의 "번역 양보"·"광고 차단"). 구버전 서버는 본문이 없다.</summary>
    private sealed record MissBody
    {
        /// <summary>이 제목은 광고로 표시돼 있다 — 제공자 검색도 업로드도 하지 않는다.</summary>
        public bool Ad { get; init; }

        public string? Error { get; init; }
        public bool Pending { get; init; }
        public int RetryAfterMs { get; init; }
    }
}
