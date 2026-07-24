using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScadaDashboard.Pipeline;

/// <summary>Ünite başına AI/DB alanları — payload.nodes[] tamamı.</summary>
public readonly record struct AiPrediction(
    double Health,
    double Rul,
    double Vibration,
    double BearingTemp,
    double DischargePressure,
    string DisplayId);

/// <summary>Segment başına AI alanları — payload.segments[] tamamı.</summary>
public readonly record struct AiSegOverlay(double FlowM3H, double Load, bool Leak);

/// <summary>AI respond sarmalayıcı özeti.</summary>
public sealed record AiRespondStatus(
    long RespondId,
    DateTime? FetchedAt,
    DateTime? ModelUpdatedAt,
    int NodeCount,
    int SegmentCount,
    int CriticalNodeCount,
    int LeakSegmentCount,
    double MinHealth,
    double MinRul);

/// <summary>AI/DB'den türetilen alarm. Kind: critical_health | leak.</summary>
public sealed record AiAlarm(
    string Id,
    string EntityId,
    string Kind,
    string Severity,
    string Message,
    double? Health,
    double? Rul);

/// <summary>Tek poll: predictions + segment overlay + durum + alarmlar.</summary>
public sealed record AiRespondSnapshot(
    IReadOnlyDictionary<string, AiPrediction> Predictions,
    IReadOnlyDictionary<string, AiSegOverlay> Segments,
    AiRespondStatus Status,
    IReadOnlyList<AiAlarm> Alarms);

/// <summary>
/// AI respond istemcisi — doğru sözleşme (Kişi 4 / Tailscale notu):
///   GET /health                         → latest_respond_id
///   GET /ai_respond?since=N&amp;limit=M → items + next_since
/// Her turda <c>next_since</c> ile imleç ilerletilir; <c>/ai_respond/latest</c>
/// tekrar tekrar çağrılmaz (aynı satırı döner).
/// </summary>
public sealed class AiRespondService : IDisposable
{
    public const double CriticalHealthThreshold = 20.0;
    private const int PageLimit = 50;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _base; // http://host:9000  (path yok)
    private readonly int _pollSeconds;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Son işlenen satır imleci — bir sonraki istek <c>since=_since</c>.</summary>
    private long _since;

    public event Action<AiRespondSnapshot>? SnapshotReceived;

    /// <param name="url">
    /// Taban (<c>http://host:9000</c>) veya eski <c>.../ai_respond/latest</c> —
    /// her ikisi de tabana indirgenir.
    /// </param>
    public AiRespondService(string url, int pollSeconds = 5, int timeoutSeconds = 15)
    {
        _base = NormalizeBase(url);
        _pollSeconds = Math.Max(1, pollSeconds);
        _http = new HttpClient
        {
            BaseAddress = new Uri(_base.EndsWith('/') ? _base : _base + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)),
        };
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private static string NormalizeBase(string url)
    {
        var u = new Uri(url.Trim());
        // Path /ai_respond/... ise yalnız authority kullan.
        return u.GetLeftPart(UriPartial.Authority);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // İlk imleç: /health → latest_respond_id; bir önceki satırdan başla ki
        // hemen güncel payload gelsin. Health yoksa since=0.
        try
        {
            long latest = await FetchLatestIdAsync(ct).ConfigureAwait(false);
            _since = latest > 0 ? latest - 1 : 0;
        }
        catch { _since = 0; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await FetchSinceAsync(ct).ConfigureAwait(false);
                if (snap != null)
                    SnapshotReceived?.Invoke(snap);
            }
            catch { /* son iyi overlay kalır; imleç ilerletilmez (yeniden dene) */ }

            try { await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<long> FetchLatestIdAsync(CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync("health", ct).ConfigureAwait(false);
        var h = await JsonSerializer.DeserializeAsync<HealthDto>(stream, JsonOpt, ct)
            .ConfigureAwait(false);
        return h?.LatestRespondId ?? 0;
    }

    private async Task<AiRespondSnapshot?> FetchSinceAsync(CancellationToken ct)
    {
        // Geri kaldıysa tek poll'da yakala — aksi halde harita eski health gösterir.
        AiRespondWrapper? newest = null;
        for (int pageNo = 0; pageNo < 40; pageNo++)
        {
            string path = $"ai_respond?since={_since}&limit={PageLimit}";
            await using var stream = await _http.GetStreamAsync(path, ct).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync<SincePage>(stream, JsonOpt, ct)
                .ConfigureAwait(false);
            if (page == null) break;

            long next = ResolveNextSince(page, _since);
            if (next > _since)
                _since = next;

            if (page.Items == null || page.Items.Count == 0)
                break;

            newest = page.Items[^1];
            // Tam sayfa değilse güncele yetiştik.
            if (page.Items.Count < PageLimit)
                break;
        }

        return newest == null ? null : BuildSnapshot(newest);
    }

    /// <summary>
    /// API snake_case <c>next_since</c> verir. Eski/yanlış deserialize için
    /// yedek: sayfadaki max <c>respond_id</c>.
    /// </summary>
    private static long ResolveNextSince(SincePage page, long currentSince)
    {
        if (page.NextSince > currentSince)
            return page.NextSince;
        if (page.Items == null || page.Items.Count == 0)
            return currentSince;
        long maxId = currentSince;
        foreach (var it in page.Items)
            if (it.RespondId > maxId) maxId = it.RespondId;
        return maxId;
    }

    private static AiRespondSnapshot? BuildSnapshot(AiRespondWrapper wrap)
    {
        if (wrap.Payload == null) return null;

        var nodes = new Dictionary<string, AiPrediction>(StringComparer.Ordinal);
        var segs = new Dictionary<string, AiSegOverlay>(StringComparer.Ordinal);
        var alarms = new List<AiAlarm>();

        if (wrap.Payload.Nodes != null)
        {
            foreach (var n in wrap.Payload.Nodes)
            {
                if (string.IsNullOrWhiteSpace(n.Id)) continue;
                nodes[SnapshotLoader.Norm(n.Id)] = new AiPrediction(
                    n.Health, n.Rul, n.Vibration, n.BearingTemp, n.DischargePressure, n.Id);

                if (n.Health < CriticalHealthThreshold)
                {
                    alarms.Add(new AiAlarm(
                        Id: $"node:{n.Id}",
                        EntityId: n.Id,
                        Kind: "critical_health",
                        Severity: "critical",
                        Message: $"Sağlık %{n.Health:0.#} · RUL {n.Rul:0.#} · vib {n.Vibration:0.##}",
                        Health: n.Health,
                        Rul: n.Rul));
                }
            }
        }

        if (wrap.Payload.Segments != null)
        {
            foreach (var s in wrap.Payload.Segments)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) continue;
                segs[SnapshotLoader.Norm(s.Id)] = new AiSegOverlay(s.Flow, Math.Clamp(s.Load, 0, 1), s.Leak);
                if (!s.Leak) continue;
                alarms.Add(new AiAlarm(
                    Id: $"seg:{s.Id}",
                    EntityId: s.Id,
                    Kind: "leak",
                    Severity: "critical",
                    Message: $"Sızıntı (AI) · akış {s.Flow:0} · yük %{s.Load * 100:0}",
                    Health: null,
                    Rul: null));
            }
        }

        alarms.Sort((a, b) =>
        {
            int ka = a.Kind == "critical_health" ? 0 : 1;
            int kb = b.Kind == "critical_health" ? 0 : 1;
            if (ka != kb) return ka.CompareTo(kb);
            return (a.Health ?? 0).CompareTo(b.Health ?? 0);
        });

        var status = new AiRespondStatus(
            wrap.RespondId,
            ParseTime(wrap.FetchedAt),
            ParseTime(wrap.ModelUpdatedAt),
            wrap.NodeCount,
            wrap.SegmentCount,
            wrap.CriticalNodeCount,
            wrap.LeakSegmentCount,
            wrap.MinHealth,
            wrap.MinRul);

        return new AiRespondSnapshot(nodes, segs, status, alarms);
    }

    private static DateTime? ParseTime(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* iptal */ }
        _cts.Dispose();
        _http.Dispose();
    }

    // --- DTOs ----------------------------------------------------------------
    // Wrapper/page alanları snake_case; node alanları camelCase (bearingTemp).
    // CaseInsensitive snake_case'i eşlemez — JsonPropertyName zorunlu.

    private sealed class HealthDto
    {
        [JsonPropertyName("latest_respond_id")]
        public long LatestRespondId { get; set; }
    }

    private sealed class SincePage
    {
        public int Count { get; set; }
        [JsonPropertyName("next_since")]
        public long NextSince { get; set; }
        public List<AiRespondWrapper>? Items { get; set; }
    }

    private sealed class AiRespondWrapper
    {
        [JsonPropertyName("respond_id")]
        public long RespondId { get; set; }
        [JsonPropertyName("fetched_at")]
        public string? FetchedAt { get; set; }
        [JsonPropertyName("model_updated_at")]
        public string? ModelUpdatedAt { get; set; }
        [JsonPropertyName("node_count")]
        public int NodeCount { get; set; }
        [JsonPropertyName("segment_count")]
        public int SegmentCount { get; set; }
        [JsonPropertyName("critical_node_count")]
        public int CriticalNodeCount { get; set; }
        [JsonPropertyName("leak_segment_count")]
        public int LeakSegmentCount { get; set; }
        [JsonPropertyName("min_health")]
        public double MinHealth { get; set; }
        [JsonPropertyName("min_rul")]
        public double MinRul { get; set; }
        public AiRespondPayload? Payload { get; set; }
    }

    private sealed class AiRespondPayload
    {
        public List<AiRespondNode>? Nodes { get; set; }
        public List<AiRespondSeg>? Segments { get; set; }
    }

    private sealed class AiRespondNode
    {
        public string Id { get; set; } = "";
        public double Health { get; set; }
        public double Rul { get; set; }
        public double Vibration { get; set; }
        public double BearingTemp { get; set; }
        public double DischargePressure { get; set; }
    }

    private sealed class AiRespondSeg
    {
        public string Id { get; set; } = "";
        public double Flow { get; set; }
        public double Load { get; set; }
        public bool Leak { get; set; }
    }
}
