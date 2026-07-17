using System.Net.Http;
using Microsoft.Extensions.Options;
using ScadaClient.Options;
using ScadaClient.Parsing;
using ScadaClient.Services;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Canlı telemetri kaynağı — veri yolu Kişi 2'nin resmi istemcisi (ScadaClient):
/// LiveDataService arka planda /api/scada/live_data'yı yoklar (PollIntervalSeconds),
/// SnapshotReceived olayı tipli DTO'ları getirir; TelemetryAdapter bunları UI'ın
/// sensör sözlüğüne çevirir ve telemetri atomik değiştirilir. Topoloji
/// (düğüm/segment/koordinat/ünite) kurucuya dışarıdan verilir (API veya dosya).
///
/// Sağlık/RUL/akış/sızıntı türetme mantığı SnapshotSource ile aynıdır (proxy).
/// Endpoint erişilemezse istemci turu atlar, SON İYİ telemetri korunur —
/// harita her koşulda çalışır. Kaynak değişiminde Dispose() yoklamayı durdurur.
/// </summary>
public sealed class LiveSnapshotSource : SnapshotSource, IDisposable
{
    private readonly ScadaApiClient _api;      // HttpClient'ın sahibi
    private readonly LiveDataService _service; // arka plan yoklayıcı (istemcinin tasarımı)
    private readonly NodeDetailClient _detail; // talep üzerine B ucu (/node/{id})

    public LiveSnapshotSource(
        SnapshotData topology, string baseUrl,
        int pollSeconds = 5, int timeoutSeconds = 15) : base(topology)
    {
        // İstemcinin tüm seçenekleri (BaseUrl + poll aralığı + zaman aşımı) uygulama
        // ayarlarından beslenir — hiçbiri artık sabit kodlu değil.
        var opt = new ScadaApiOptions
        {
            BaseUrl = baseUrl,
            PollIntervalSeconds = pollSeconds,
            TimeoutSeconds = timeoutSeconds,
        };
        var http = new HttpClient
        {
            BaseAddress = new Uri(opt.BaseUrl),
            Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds),
        };
        _api = new ScadaApiClient(http, new ScadaJsonParser());
        _detail = new NodeDetailClient(_api);
        // İstemcinin kendi günlüğünü diske al (poll'un istemci üzerinden çalıştığı
        // çalışma anında görünür olsun; hata turları da yakalanır).
        var log = new FileLogger<LiveDataService>(
            System.IO.Path.Combine(AppContext.BaseDirectory, "scadaclient.log"));
        _service = new LiveDataService(_api, Options.Create(opt), log);
        _service.SnapshotReceived += live =>
        {
            var maps = TelemetryAdapter.ToSensorMaps(live);
            SetTelemetry(maps.Values, maps.Quality); // değerler + kalite birlikte
        };
        _ = _service.StartAsync(CancellationToken.None); // ilk çekiş hemen, sonra periyodik
    }

    // Tick() kasıtlı override edilmez: yoklamayı UI değil LiveDataService zamanlar.

    /// <summary>
    /// Talep üzerine B ucunu (/api/scada/node/{id}) çeker — haritada bir düğüme
    /// tıklanınca. Tek çağrıda NodeDetail'in TAMAMI tüketilir:
    ///   • health_state → sunucunun yetkili sağlık değerlendirmesi (döner)
    ///   • telemetry     → varsa taze düğüm telemetrisi canlı haritaya bindirilir
    ///                     (sunucu genelde boş [] gönderir; doluysa poll'dan tazedir)
    /// Canlı poll (/live_data) health_state taşımaz; yalnız bu uçta gelir. Hata/boşsa
    /// null döner ve UI titreşim proxy'sine düşer. Bloklamaz — çağıran await eder.
    /// </summary>
    public async Task<string?> FetchNodeDetailAsync(string nodeId)
    {
        try
        {
            var d = await _detail.LoadAsync(nodeId);
            if (d.Telemetry.Count > 0)
            {
                var maps = TelemetryAdapter.ToSensorMaps(d.Telemetry);
                MergeTelemetry(maps.Values, maps.Quality);
            }
            return string.IsNullOrWhiteSpace(d.HealthState) ? null : d.HealthState;
        }
        catch { return null; } // uç erişilemez/id yok — proxy'ye düş
    }

    public void Dispose()
    {
        _service.Dispose(); // yoklama döngüsünü iptal eder (BackgroundService)
        _api.Dispose();
    }
}
