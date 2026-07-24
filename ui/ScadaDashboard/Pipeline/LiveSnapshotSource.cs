using System.Net.Http;
using Microsoft.Extensions.Options;
using ScadaClient.Options;
using ScadaClient.Parsing;
using ScadaClient.Services;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Canlı telemetri + AI respond (sağlık/RUL). Yerel proxy / snapshot dosyası yok.
/// </summary>
public sealed class LiveSnapshotSource : SnapshotSource, IDisposable
{
    private readonly ScadaApiClient _api;      // HttpClient'ın sahibi
    private readonly LiveDataService _service; // arka plan yoklayıcı (istemcinin tasarımı)
    private readonly NodeDetailClient _detail; // talep üzerine B ucu (/node/{id})
    private readonly AiRespondService? _ai;    // health/RUL + durum + alarm (opsiyonel)

    // AI poll arka plan thread'inden yazar; UI okur — referans takası yeter.
    private AiRespondStatus? _aiStatus;
    private IReadOnlyList<AiAlarm> _aiAlarms = Array.Empty<AiAlarm>();

    /// <summary>Son AI sarmalayıcı özeti (yoksa null).</summary>
    public AiRespondStatus? AiStatus => _aiStatus;

    /// <summary>AI'dan türetilen açık alarmlar (kritik sağlık + sızıntı).</summary>
    public IReadOnlyList<AiAlarm> AiAlarms => _aiAlarms;

    /// <summary>AI overlay etkin ve en az bir prediction alındı mı.</summary>
    public bool HasAiOverlay => _ai != null && _aiStatus != null;

    public LiveSnapshotSource(
        SnapshotData topology, string baseUrl,
        int pollSeconds = 5, int timeoutSeconds = 15,
        string? aiRespondUrl = null) : base(topology)
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
            SetTelemetry(maps.Values, maps.Quality, maps.Status); // değerler + kalite + pompa status
        };
        _ = _service.StartAsync(CancellationToken.None); // ilk çekiş hemen, sonra periyodik

        // AI/DB overlay: live_data'da olmayan health/rul + durum + alarmlar.
        if (!string.IsNullOrWhiteSpace(aiRespondUrl)
            && Uri.TryCreate(aiRespondUrl, UriKind.Absolute, out _))
        {
            _ai = new AiRespondService(aiRespondUrl, pollSeconds, timeoutSeconds);
            _ai.SnapshotReceived += OnAiSnapshot;
        }
    }

    private void OnAiSnapshot(AiRespondSnapshot snap)
    {
        SetAiOverlay(snap.Predictions, snap.Segments);
        _aiStatus = snap.Status;
        _aiAlarms = snap.Alarms;
    }

    // Tick() kasıtlı override edilmez: yoklamayı UI değil LiveDataService / AiRespondService zamanlar.

    /// <summary>
    /// Talep üzerine B ucunu (/api/scada/node/{id}) çeker — yalnızca telemetri bindirir.
    /// Sağlık/RUL live_data ve B ucunda yok; AI respond overlay kullanılır.
    /// </summary>
    public async Task FetchNodeDetailTelemetryAsync(string nodeId)
    {
        try
        {
            var d = await _detail.LoadAsync(nodeId);
            if (d.Telemetry.Count > 0)
            {
                var maps = TelemetryAdapter.ToSensorMaps(d.Telemetry);
                MergeTelemetry(maps.Values, maps.Quality, maps.Status);
            }
        }
        catch { /* uç erişilemez — canlı poll yeterli */ }
    }

    public void Dispose()
    {
        if (_ai != null)
        {
            _ai.SnapshotReceived -= OnAiSnapshot;
            _ai.Dispose();
        }
        _service.Dispose(); // yoklama döngüsünü iptal eder (BackgroundService)
        _api.Dispose();
    }
}
