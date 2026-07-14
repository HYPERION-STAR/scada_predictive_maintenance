namespace ScadaDashboard.Pipeline;

/// <summary>Bir düğümün anlık durumu (istasyonlar için sağlık/RUL).</summary>
public readonly record struct NodeSnap(double Health, double Rul, bool IsStation);

/// <summary>Bir segmentin anlık durumu (akış + doluluk oranı 0..1).</summary>
public readonly record struct SegSnap(double FlowMcmDay, double LoadRatio);

/// <summary>İstasyon detay sensörleri (kart grafikleri için).</summary>
public readonly record struct StationSensors(double Vibration, double BearingTemp, double DischargePressure);

/// <summary>
/// Canlı harita veri kaynağı soyutlaması. Bugün SIMULE; ileride gerçek akışa
/// (Kişi 2 MQTT + Kişi 1 model) bağlanacak — harita değişmeden.
/// </summary>
public interface IPipelineSource
{
    void Tick();
    NodeSnap Node(string id);
    SegSnap Segment(string id);
    StationSensors Sensors(string id);
}

/// <summary>
/// Basit, kendine yeten harita simülasyonu. İstasyonların (CS) sağlığı zamanla
/// azalır, 0'a inince otomatik bakımla toparlanır; segment akışları günlük
/// profille dalgalanır. Makine panelindeki SimulatorDataSource ile aynı ruh.
/// </summary>
public sealed class PipelineSimulator : IPipelineSource
{
    private const double MaxRul = 130.0;

    private sealed class Station
    {
        public double Rul;
        public double DegradePerTick;
        public int FailedTicks;
    }

    private readonly Dictionary<string, Station> _stations = new();
    private readonly Dictionary<string, double> _segBaseFlow = new();
    private readonly Random _rng = new(42);
    private int _tick;

    public PipelineSimulator()
    {
        // İstasyonları farklı başlangıç sağlıklarıyla kur (harita çeşitli görünsün).
        _stations["N2"] = new Station { Rul = 120, DegradePerTick = 0.25 };
        _stations["N4"] = new Station { Rul = 22, DegradePerTick = 0.32 };

        // Segment taban akışları (mcm/gün).
        _segBaseFlow["S1"] = 55; _segBaseFlow["S2"] = 50; _segBaseFlow["S3"] = 22;
        _segBaseFlow["S4"] = 30; _segBaseFlow["S5"] = 18; _segBaseFlow["S6"] = 20;
    }

    public void Tick()
    {
        _tick++;
        foreach (var s in _stations.Values)
        {
            if (s.Rul <= 0)
            {
                s.FailedTicks++;
                if (s.FailedTicks >= 6) { s.Rul = MaxRul; s.FailedTicks = 0; }
            }
            else
            {
                double d = 1.0 - s.Rul / MaxRul;
                double jitter = 0.7 + _rng.NextDouble() * 0.6;
                s.Rul = Math.Max(0, s.Rul - s.DegradePerTick * (1 + d) * jitter);
            }
        }
    }

    public NodeSnap Node(string id)
    {
        if (_stations.TryGetValue(id, out var s))
        {
            double health = Math.Clamp(s.Rul / MaxRul * 100.0, 0, 100);
            return new NodeSnap(Math.Round(health, 1), Math.Round(s.Rul, 1), true);
        }
        return new NodeSnap(100, MaxRul, false); // sınır/çıkış/kavşak: sağlık yok
    }

    public SegSnap Segment(string id)
    {
        double baseFlow = _segBaseFlow.TryGetValue(id, out var b) ? b : 30;
        // Günlük profil: sabah/akşam piki + hafif gürültü.
        double hour = _tick % 24;
        double peak = Math.Max(0, Math.Sin(2 * Math.PI * (hour - 6) / 24));
        double flow = baseFlow * (0.75 + 0.35 * peak) + (_rng.NextDouble() - 0.5) * 2;
        // Doluluk mutlak kapasiteye gore (ag geneli ~60 mcm/gun); boylece dusuk
        // akisli hatlar teal, yogun hatlar kirmizi gorunur.
        double load = Math.Clamp(flow / 60.0, 0, 1);
        return new SegSnap(Math.Round(flow, 1), load);
    }

    public StationSensors Sensors(string id)
    {
        if (_stations.TryGetValue(id, out var s))
        {
            double d = Math.Clamp(1.0 - s.Rul / MaxRul, 0, 1);
            double vib = 2.0 + d * 7.0 + Noise(0.2);
            double bt = 70.0 + d * 28.0 + Noise(0.6);
            double dp = Math.Max(1.0, 75.0 - d * 10.0 + Noise(0.3));
            return new StationSensors(Math.Round(vib, 2), Math.Round(bt, 1), Math.Round(dp, 2));
        }
        return default;
    }

    private double Noise(double a) => (_rng.NextDouble() - 0.5) * 2 * a;
}
