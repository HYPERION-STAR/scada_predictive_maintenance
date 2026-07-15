namespace ScadaDashboard.Pipeline;

/// <summary>
/// Statik snapshot'tan beslenen harita kaynağı. Snapshot'ta sağlık/RUL yok
/// (onlar AI'ın çıktısı, DB predictions tablosuyla gelecek) — o yüzden
/// istasyon sağlığı en kötü ünitenin titreşiminden PROXY olarak türetilir:
/// 2 mm/s → %100, 9 mm/s → %0. Anlık görüntü olduğundan Tick() no-op.
/// </summary>
public sealed class SnapshotSource : IPipelineSource
{
    private const double VibHealthy = 2.0;   // mm/s → %100
    private const double VibDead = 9.0;      // mm/s → %0
    private const double LeakImbalancePct = 1.5;
    private const double MaxRul = 130.0;     // simülatörle aynı RUL ölçeği

    private readonly SnapshotData _data;
    private readonly Dictionary<string, double> _segCapacity = new();

    public SnapshotSource(SnapshotData data)
    {
        _data = data;
        foreach (var s in data.Segments) _segCapacity[s.Id] = s.MaxCapacity;
    }

    public void Tick() { } // statik anlık görüntü — zaman ilerlemez

    private IReadOnlyDictionary<string, double>? Telem(string id) =>
        _data.Telemetry.TryGetValue(SnapshotLoader.Norm(id), out var t) ? t : null;

    // İstasyonun en kötü (en yüksek titreşimli) ünitesinin telemetrisi.
    private IReadOnlyDictionary<string, double>? WorstUnit(string stationId)
    {
        if (!_data.StationUnits.TryGetValue(stationId, out var units)) return null;
        IReadOnlyDictionary<string, double>? worst = null;
        double worstVib = -1;
        foreach (var uid in units)
        {
            var t = Telem(uid);
            if (t == null) continue;
            double vib = t.GetValueOrDefault("s_7_vibration_de_mm_s");
            if (vib > worstVib) { worstVib = vib; worst = t; }
        }
        return worst;
    }

    public NodeSnap Node(string id)
    {
        var w = WorstUnit(id);
        if (w == null) return new NodeSnap(100, MaxRul, false); // sınır/çıkış/kavşak/depo

        double vib = w.GetValueOrDefault("s_7_vibration_de_mm_s");
        double health = Math.Clamp((VibDead - vib) / (VibDead - VibHealthy) * 100.0, 0, 100);
        // RUL proxy: DB predictions gelene kadar sağlıkla orantılı gösterim.
        return new NodeSnap(Math.Round(health, 1), Math.Round(health / 100.0 * MaxRul, 0), true);
    }

    public SegSnap Segment(string id)
    {
        var t = Telem(id);
        if (t == null) return new SegSnap(0, 0, false);

        double flowM3H = t.GetValueOrDefault("s_flow_m3_h");
        double flowMcmDay = flowM3H * 24.0 / 1e6; // m³/saat → mcm/gün

        // Doluluk: snapshot kapasiteleri bin m³/gün ölçeğiyle uyumlu
        // (ör. 420000 m³/sa ≈ 10080 vs kapasite 15000 → ~0.67).
        double cap = _segCapacity.GetValueOrDefault(id);
        double load = cap > 0 ? Math.Clamp(flowM3H * 24.0 / 1000.0 / cap, 0, 1) : 0;

        bool leak = t.GetValueOrDefault("s_mass_imbalance_pct") > LeakImbalancePct;
        return new SegSnap(Math.Round(flowMcmDay, 1), load, leak);
    }

    public StationSensors Sensors(string id)
    {
        var w = WorstUnit(id);
        if (w == null) return default;
        return new StationSensors(
            w.GetValueOrDefault("s_7_vibration_de_mm_s"),
            w.GetValueOrDefault("s_10_bearing_temp_1_c"),
            w.GetValueOrDefault("s_2_discharge_pressure_bar"));
    }

    // Depo doluluk yüzdesi (UGS telemetrisinden).
    public double Level(string id) =>
        Telem(id)?.GetValueOrDefault("s_storage_level_pct") ?? 0;
}
