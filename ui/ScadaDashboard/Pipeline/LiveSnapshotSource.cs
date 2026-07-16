using System.Net.Http;
using System.Text.Json;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Canlı telemetri kaynağı. Topolojiyi (düğüm/segment/koordinat/ünite) statik
/// snapshot dosyasından BİR KEZ alır; her Tick()'te canlı endpoint'ten düz
/// entity→sensör JSON'unu çekip telemetriyi tazeler. Sağlık/RUL/akış/sızıntı
/// türetme mantığı SnapshotSource ile aynıdır (proxy) — yalnızca sayılar canlıdır.
///
/// Endpoint sözleşmesi — GET {url} şunu döndürür (entity_id ile anahtarlı düz sözlük):
/// {
///   "CS-ANKARA-U1":       { "entity_id":"CS-ANKARA-U1", "s_7_vibration_de_mm_s":5.4, ... },
///   "SEG-SIVAS-ERZINCAN": { "entity_id":"SEG-SIVAS-ERZINCAN", "s_flow_m3_h":432135, "s_mass_imbalance_pct":0.6, ... }
/// }
/// (Aynı sensör anahtarları statik snapshot'ın telemetry bölümüyle birebir aynıdır.)
///
/// Endpoint erişilemezse SON İYİ telemetri korunur; ilk çekiş gelene kadar
/// snapshot dosyasındaki telemetri gösterilir. Böylece harita her koşulda çalışır.
/// </summary>
public sealed class LiveSnapshotSource : SnapshotSource
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _url;

    public LiveSnapshotSource(SnapshotData topology, string url) : base(topology) => _url = url;

    // UI ~saniyede bir çağırır; canlı çekişi tetikler (fire-and-forget).
    public override void Tick() => _ = FetchAsync();

    private async Task FetchAsync()
    {
        try
        {
            await using var s = await _http.GetStreamAsync(_url);
            using var doc = await JsonDocument.ParseAsync(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            var telemetry = new Dictionary<string, IReadOnlyDictionary<string, double>>();
            foreach (var entity in doc.RootElement.EnumerateObject())
            {
                if (entity.Value.ValueKind != JsonValueKind.Object) continue;

                var vals = new Dictionary<string, double>();
                foreach (var p in entity.Value.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        vals[p.Name] = p.Value.GetDouble();

                // Hem sözlük anahtarı hem entity_id ile eriş (SnapshotLoader ile aynı kural).
                telemetry[SnapshotLoader.Norm(entity.Name)] = vals;
                if (entity.Value.TryGetProperty("entity_id", out var eid)
                    && eid.ValueKind == JsonValueKind.String)
                    telemetry[SnapshotLoader.Norm(eid.GetString()!)] = vals;
            }

            SetTelemetry(telemetry); // atomik referans değişimi
        }
        catch { /* endpoint yoksa son iyi telemetriyi koru */ }
    }
}
