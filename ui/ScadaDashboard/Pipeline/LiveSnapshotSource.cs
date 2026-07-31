using System.Net.Http;
using Microsoft.Extensions.Options;
using SCaDaClient.Models.Telemetry;
using SCaDaClient.Options;
using SCaDaClient.Parsing;
using SCaDaClient.Services;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Canlı telemetri + AI respond (sağlık/RUL).
/// Petrol: MySQL <c>live_entity_current</c> öncelikli; DB erişilemezse /live_data.
/// Gaz ve diğerleri: canlı API.
/// </summary>
public sealed class LiveSnapshotSource : SnapshotSource, IDisposable
{
    private readonly SCaDaApiClient _api;
    private readonly LiveDataService _service;
    private readonly NodeDetailClient _detail;
    private readonly AiRespondService? _ai;
    private readonly LiveEntityCurrentReader? _oilDb;
    private readonly int _pollSeconds;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Task? _oilLoop;

    private IReadOnlyDictionary<string, TelemetryBase>? _latestLive;
    private IReadOnlyDictionary<string, TelemetryBase>? _latestOilDb;
    private bool _oilFromDb;

    private AiRespondStatus? _aiStatus;
    private IReadOnlyList<AiAlarm> _aiAlarms = Array.Empty<AiAlarm>();

    public AiRespondStatus? AiStatus => _aiStatus;
    public IReadOnlyList<AiAlarm> AiAlarms => _aiAlarms;
    public bool HasAiOverlay => _ai != null && _aiStatus != null;

    /// <summary>Son başarılı petrol kaynağı MySQL mi?</summary>
    public bool OilFromDatabase
    {
        get { lock (_sync) return _oilFromDb; }
    }

    public LiveSnapshotSource(
        SnapshotData topology, string baseUrl,
        int pollSeconds = 5, int timeoutSeconds = 15,
        string? aiRespondUrl = null,
        string? databaseConnectionString = null) : base(topology)
    {
        _pollSeconds = Math.Max(1, pollSeconds);
        var opt = new SCaDaApiOptions
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
        var parser = new SCaDaJsonParser();
        _api = new SCaDaApiClient(http, parser);
        _detail = new NodeDetailClient(_api);
        var log = new FileLogger<LiveDataService>(
            System.IO.Path.Combine(AppContext.BaseDirectory, "scadaclient.log"));
        // Canlı yazım yok; petrol okuma ayrı reader ile.
        _service = new LiveDataService(_api, Options.Create(opt), repository: null, log);
        _service.SnapshotReceived += OnLiveSnapshot;
        _ = _service.StartAsync(CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(databaseConnectionString))
        {
            _oilDb = new LiveEntityCurrentReader(databaseConnectionString, parser);
            _oilLoop = Task.Run(() => OilDbLoopAsync(_cts.Token));
        }

        if (!string.IsNullOrWhiteSpace(aiRespondUrl)
            && Uri.TryCreate(aiRespondUrl, UriKind.Absolute, out _))
        {
            _ai = new AiRespondService(aiRespondUrl, pollSeconds, timeoutSeconds);
            _ai.SnapshotReceived += OnAiSnapshot;
        }
    }

    private void OnLiveSnapshot(IReadOnlyDictionary<string, TelemetryBase> live)
    {
        lock (_sync)
        {
            _latestLive = live;
            PublishMerged_NoLock();
        }
    }

    private async Task OilDbLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var oil = await _oilDb!.ReadOilAsync(ct).ConfigureAwait(false);
                lock (_sync)
                {
                    if (oil.Count > 0)
                    {
                        _latestOilDb = oil;
                        _oilFromDb = true;
                    }
                    else
                    {
                        // DB ayakta ama petrol yok → canlıya düş.
                        _latestOilDb = null;
                        _oilFromDb = false;
                    }
                    PublishMerged_NoLock();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // DB erişilemez — petrol canlı API'den gelsin.
                lock (_sync)
                {
                    if (_oilFromDb)
                    {
                        _oilFromDb = false;
                        _latestOilDb = null;
                        PublishMerged_NoLock();
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Canlı harita tabanı + (varsa) DB petrol overlay.
    /// DB petrol anahtarları canlıyı ezer; DB yoksa canlı petrol kalır.
    /// </summary>
    private void PublishMerged_NoLock()
    {
        if (_latestLive == null) return;

        if (_oilFromDb && _latestOilDb is { Count: > 0 } oilDb)
        {
            var merged = new Dictionary<string, TelemetryBase>(_latestLive.Count + oilDb.Count);
            foreach (var (k, v) in _latestLive)
            {
                // Canlı petrolü at — DB sürümü kullanılacak (çift kaynak karışmasın).
                if (v is OilPumpTelemetry or OilStorageTelemetry) continue;
                if (string.Equals(v.EntityType, "oil_pump", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.EntityType, "oil_storage", StringComparison.OrdinalIgnoreCase))
                    continue;
                merged[k] = v;
            }
            foreach (var (k, v) in oilDb)
                merged[k] = v;

            var maps = TelemetryAdapter.ToSensorMaps(merged);
            SetTelemetry(maps.Values, maps.Quality, maps.Status);
        }
        else
        {
            var maps = TelemetryAdapter.ToSensorMaps(_latestLive);
            SetTelemetry(maps.Values, maps.Quality, maps.Status);
        }
    }

    private void OnAiSnapshot(AiRespondSnapshot snap)
    {
        SetAiOverlay(snap.Predictions, snap.Segments);
        _aiStatus = snap.Status;
        _aiAlarms = snap.Alarms;
    }

    public async Task FetchNodeDetailTelemetryAsync(string nodeId)
    {
        try
        {
            var d = await _detail.LoadAsync(nodeId);
            if (d.Telemetry.Count > 0)
            {
                // DB petrol aktifken canlı node detayı petrolü ezmesin.
                if (OilFromDatabase)
                {
                    var filtered = d.Telemetry
                        .Where(kv => kv.Value is not OilPumpTelemetry
                            and not OilStorageTelemetry
                            && !string.Equals(kv.Value.EntityType, "oil_pump",
                                StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(kv.Value.EntityType, "oil_storage",
                                StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    if (filtered.Count == 0) return;
                    var maps = TelemetryAdapter.ToSensorMaps(filtered);
                    MergeTelemetry(maps.Values, maps.Quality, maps.Status);
                }
                else
                {
                    var maps = TelemetryAdapter.ToSensorMaps(d.Telemetry);
                    MergeTelemetry(maps.Values, maps.Quality, maps.Status);
                }
            }
        }
        catch { /* uç erişilemez — poll yeterli */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_oilLoop != null)
        {
            try { _oilLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* iptal */ }
        }
        _cts.Dispose();

        if (_ai != null)
        {
            _ai.SnapshotReceived -= OnAiSnapshot;
            _ai.Dispose();
        }
        _service.SnapshotReceived -= OnLiveSnapshot;
        _service.Dispose();
        _api.Dispose();
    }
}
