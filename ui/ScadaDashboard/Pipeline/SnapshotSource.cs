using ScadaDashboard.Models;

namespace ScadaDashboard.Pipeline;

/// <summary>Ünite başına performans özeti (gaz: AI health/RUL; petrol: status/flow/power + canlı).</summary>
public readonly record struct UnitPerf(
    string UnitId,
    double Health,
    double Rul,
    double Vibration,
    double BearingTemp,
    double DischargePressure,
    bool HasAi,
    bool HasLive,
    bool IsOilPump = false,
    string OpStatus = "",
    double FlowM3H = 0,
    double PumpPowerMw = 0,
    double SuctionPressure = 0);

/// <summary>
/// Harita kaynağı — tüm anlamlı değerler sunucudan:
///   • Topoloji + canlı sensörler (titreşim/sıcaklık/basınç): live API
///   • Sağlık / RUL / segment flow·load·leak: AI respond overlay
/// AI titreşim canlıyı ezmez (AI sıkça 9.0'a sıkıştırır). Yerel proxy / snapshot yok.
/// </summary>
public class SnapshotSource : IPipelineSource
{
    private readonly SnapshotData _data;

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> _telemetry;
    private IReadOnlyDictionary<string, string> _quality = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _opStatus = new Dictionary<string, string>();

    private IReadOnlyDictionary<string, AiPrediction> _ai =
        new Dictionary<string, AiPrediction>();
    private IReadOnlyDictionary<string, AiSegOverlay> _aiSeg =
        new Dictionary<string, AiSegOverlay>();

    public SnapshotSource(SnapshotData data)
    {
        _data = data;
        _telemetry = data.Telemetry;
    }

    public virtual void Tick() { }

    protected void SetTelemetry(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> telemetry,
        IReadOnlyDictionary<string, string> quality,
        IReadOnlyDictionary<string, string>? opStatus = null)
    {
        _telemetry = telemetry;
        _quality = quality;
        if (opStatus != null) _opStatus = opStatus;
    }

    protected void MergeTelemetry(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> overlay,
        IReadOnlyDictionary<string, string> quality,
        IReadOnlyDictionary<string, string>? opStatus = null)
    {
        if (overlay.Count == 0) return;
        var mergedTel = new Dictionary<string, IReadOnlyDictionary<string, double>>(_telemetry);
        foreach (var kv in overlay) mergedTel[kv.Key] = kv.Value;
        var mergedQ = new Dictionary<string, string>(_quality);
        foreach (var kv in quality) mergedQ[kv.Key] = kv.Value;
        _telemetry = mergedTel;
        _quality = mergedQ;
        if (opStatus is { Count: > 0 })
        {
            var mergedS = new Dictionary<string, string>(_opStatus);
            foreach (var kv in opStatus) mergedS[kv.Key] = kv.Value;
            _opStatus = mergedS;
        }
    }

    /// <summary>AI respond — yalnız health/RUL + segment overlay. Canlı sensörleri yazmaz.</summary>
    protected void SetAiOverlay(
        IReadOnlyDictionary<string, AiPrediction> predictions,
        IReadOnlyDictionary<string, AiSegOverlay> segments)
    {
        _ai = predictions;
        _aiSeg = segments;
    }

    protected void SetAiPredictions(IReadOnlyDictionary<string, AiPrediction> predictions) =>
        SetAiOverlay(predictions, _aiSeg);

    private bool TryAi(string id, out AiPrediction p) =>
        _ai.TryGetValue(SnapshotLoader.Norm(id), out p);

    private bool TryAiSeg(string id, out AiSegOverlay s) =>
        _aiSeg.TryGetValue(SnapshotLoader.Norm(id), out s);

    private IReadOnlyDictionary<string, double>? Telem(string id) =>
        _telemetry.TryGetValue(SnapshotLoader.Norm(id), out var t) ? t : null;

    public string UnitOpStatus(string unitId) =>
        _opStatus.TryGetValue(SnapshotLoader.Norm(unitId), out var s) ? s : "";

    /// <summary>Gaz veya petrol pompa anahtarlarından ilk bulunan değer.</summary>
    private static double Pick(IReadOnlyDictionary<string, double> t, params string[] keys)
    {
        foreach (var k in keys)
            if (t.TryGetValue(k, out var v)) return v;
        return 0;
    }

    private static bool LooksLikeOilPump(IReadOnlyDictionary<string, double> t) =>
        t.ContainsKey("s_vibration_mm_s") || t.ContainsKey("s_pump_power_mw")
        || t.ContainsKey("s_flow_m3_h") && t.ContainsKey("s_suction_pressure_bar");

    /// <summary>AI overlay'de istasyonun en kötü (min health) ünitesi.</summary>
    public (string UnitId, AiPrediction Pred)? WorstAiUnit(string stationId)
    {
        (string UnitId, AiPrediction Pred)? worst = null;

        void Consider(string unitId, AiPrediction p)
        {
            if (worst == null || p.Health < worst.Value.Pred.Health)
                worst = (unitId, p);
        }

        if (_data.StationUnits.TryGetValue(stationId, out var units))
        {
            foreach (var u in units)
                if (TryAi(u.Id, out var p)) Consider(u.Id, p);
        }

        if (worst == null && _ai.Count > 0)
        {
            string nRoot = SnapshotLoader.Norm(StationRoot(stationId));
            foreach (var kv in _ai)
            {
                if (!kv.Key.StartsWith(nRoot + "_u", StringComparison.Ordinal)
                    && !kv.Key.Equals(nRoot, StringComparison.Ordinal))
                    continue;
                Consider(kv.Value.DisplayId.Length > 0 ? kv.Value.DisplayId : kv.Key, kv.Value);
            }
        }

        return worst;
    }

    private static string StationRoot(string stationId)
    {
        int last = stationId.LastIndexOf('-');
        if (last > 0 && stationId.Length > last + 1
            && stationId.AsSpan(last + 1).IndexOfAny("0123456789".AsSpan()) >= 0
            && stationId.AsSpan(last + 1).IndexOfAny("Uu".AsSpan()) < 0)
            return stationId[..last];
        return stationId;
    }

    /// <summary>AI sayısal sağlıktan görünen etiket (sunucu değerinin sınıflaması).</summary>
    public static string AiHealthStateLabel(double healthPercent) =>
        healthPercent < 20 ? "kritik"
        : healthPercent < 40 ? "riskli"
        : healthPercent < 70 ? "uyarı"
        : "sağlıklı";

    public string StationDataQuality(string stationId)
    {
        if (!_data.StationUnits.TryGetValue(stationId, out var units)) return "";
        string seen = "";
        foreach (var u in units)
        {
            if (!_quality.TryGetValue(SnapshotLoader.Norm(u.Id), out var q) || q.Length == 0) continue;
            if (!q.Equals("GOOD", StringComparison.OrdinalIgnoreCase)) return q;
            seen = q;
        }
        return seen;
    }

    /// <summary>En kötü AI ünitesinin canlı SCADA sensörleri (AI titreşim yedeklenmez).</summary>
    public (string UnitId, IReadOnlyDictionary<string, double> Sensors)? WorstUnitSensors(string stationId)
    {
        var ai = WorstAiUnit(stationId);
        if (ai != null)
        {
            var t = Telem(ai.Value.UnitId);
            if (t != null && t.Count > 0) return (ai.Value.UnitId, t);
        }

        if (!_data.StationUnits.TryGetValue(stationId, out var units)) return null;
        foreach (var u in units)
        {
            var t = Telem(u.Id);
            if (t != null && t.Count > 0) return (u.Id, t);
        }
        return null;
    }

    public NodeSnap Node(string id)
    {
        var aiWorst = WorstAiUnit(id);
        if (aiWorst != null)
            return new NodeSnap(
                Math.Round(aiWorst.Value.Pred.Health, 1),
                Math.Round(aiWorst.Value.Pred.Rul, 0),
                true);

        if (TryAi(id, out var stationAi))
            return new NodeSnap(
                Math.Round(stationAi.Health, 1),
                Math.Round(stationAi.Rul, 0),
                true);

        // Petrol pompa: AI yok — canlı sensör kondisyon skoru (pasta/harita için).
        var oilUnits = UnitHealths(id);
        double worstOil = double.NaN;
        foreach (var u in oilUnits)
        {
            if (!u.HasTelemetry) continue;
            if (double.IsNaN(worstOil) || u.Health < worstOil) worstOil = u.Health;
        }
        if (!double.IsNaN(worstOil))
            return new NodeSnap(Math.Round(worstOil, 1), 0, true);

        bool station = _data.StationUnits.ContainsKey(id) || InferTypeIsCs(id);
        return new NodeSnap(100, 0, station);
    }

    private static bool InferTypeIsCs(string id) =>
        id.StartsWith("CS-", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<UnitHealth> UnitHealths(string id)
    {
        if (!_data.StationUnits.TryGetValue(id, out var units) || units.Count == 0)
        {
            var fromAi = new List<UnitHealth>();
            string root = SnapshotLoader.Norm(StationRoot(id));
            foreach (var kv in _ai)
            {
                if (!kv.Key.StartsWith(root + "_u", StringComparison.Ordinal)) continue;
                string did = kv.Value.DisplayId.Length > 0 ? kv.Value.DisplayId : kv.Key;
                fromAi.Add(new UnitHealth(did, did, Math.Round(kv.Value.Health, 1), true));
            }
            return fromAi;
        }

        var result = new List<UnitHealth>(units.Count);
        foreach (var u in units)
        {
            if (TryAi(u.Id, out var ai))
            {
                result.Add(new UnitHealth(u.Id, u.Id, Math.Round(ai.Health, 1), true));
                continue;
            }

            var t = Telem(u.Id);
            if (t != null && t.Count > 0 && LooksLikeOilPump(t))
            {
                // Petrol: AI health yok — titreşim/yatak’tan görsel kondisyon (pasta dilimi).
                result.Add(new UnitHealth(u.Id, u.Id, OilConditionScore(t), true));
                continue;
            }

            result.Add(new UnitHealth(u.Id, u.Id, 100, false));
        }
        return result;
    }

    /// <summary>
    /// Petrol pompa görsel skoru (0–100). AI health yerine canlı sensör bantları;
    /// yalnız harita pastası / özet için — gaz AI sağlığı ile karıştırılmaz.
    /// </summary>
    private static double OilConditionScore(IReadOnlyDictionary<string, double> t)
    {
        double vib = Pick(t, "s_vibration_mm_s");
        double temp = Pick(t, "s_bearing_temp_c");

        double vibScore = vib <= 2 ? 100
            : vib >= 6 ? 8
            : 100 - (vib - 2) / 4.0 * 92;
        double tempScore = temp <= 55 ? 100
            : temp >= 85 ? 12
            : 100 - (temp - 55) / 30.0 * 88;

        return Math.Round(Math.Clamp(Math.Min(vibScore, tempScore), 0, 100), 1);
    }

    public IReadOnlyList<SnapshotUnit> UnitList(string stationId) =>
        _data.StationUnits.TryGetValue(stationId, out var units)
            ? units : Array.Empty<SnapshotUnit>();

    /// <summary>İstasyon ünitelerinin AI sağlık/RUL + canlı sensör özeti (gaz + petrol).</summary>
    public IReadOnlyList<UnitPerf> UnitPerformance(string stationId)
    {
        var ids = new List<string>();
        if (_data.StationUnits.TryGetValue(stationId, out var listed) && listed.Count > 0)
        {
            foreach (var u in listed) ids.Add(u.Id);
        }
        else
        {
            string root = SnapshotLoader.Norm(StationRoot(stationId));
            foreach (var kv in _ai)
            {
                if (!kv.Key.StartsWith(root + "_u", StringComparison.Ordinal)) continue;
                ids.Add(kv.Value.DisplayId.Length > 0 ? kv.Value.DisplayId : kv.Key);
            }
        }

        var result = new List<UnitPerf>(ids.Count);
        foreach (var id in ids)
        {
            bool hasAi = TryAi(id, out var ai);
            var t = Telem(id);
            bool hasLive = t != null && t.Count > 0;
            bool isOil = hasLive && LooksLikeOilPump(t!);

            double vib = 0, temp = 0, press = 0, flow = 0, power = 0, suction = 0;
            if (hasLive)
            {
                vib = Pick(t!, "s_7_vibration_de_mm_s", "s_vibration_mm_s");
                temp = Pick(t!, "s_10_bearing_temp_1_c", "s_bearing_temp_c");
                press = Pick(t!, "s_2_discharge_pressure_bar", "s_discharge_pressure_bar");
                flow = Pick(t!, "s_flow_m3_h", "s_16_gas_flow_m3_h", "s_14_gas_flow_meter_m3_h");
                power = Pick(t!, "s_pump_power_mw", "s_17_power_mw");
                suction = Pick(t!, "s_suction_pressure_bar", "s_1_suction_pressure_bar");
            }
            else if (hasAi)
            {
                vib = ai.Vibration;
                temp = ai.BearingTemp;
                press = ai.DischargePressure;
            }

            result.Add(new UnitPerf(
                UnitId: id,
                Health: hasAi ? Math.Round(ai.Health, 1)
                    : (isOil && hasLive ? OilConditionScore(t!) : 0),
                Rul: hasAi ? Math.Round(ai.Rul, 0) : 0,
                Vibration: Math.Round(vib, 2),
                BearingTemp: Math.Round(temp, 1),
                DischargePressure: Math.Round(press, 2),
                HasAi: hasAi,
                HasLive: hasLive,
                IsOilPump: isOil,
                OpStatus: UnitOpStatus(id),
                FlowM3H: Math.Round(flow, 0),
                PumpPowerMw: Math.Round(power, 2),
                SuctionPressure: Math.Round(suction, 2)));
        }
        return result;
    }

    /// <summary>Tek ünite canlı sensörleri (yoksa boş).</summary>
    public IReadOnlyDictionary<string, double>? UnitTelemetry(string unitId) => Telem(unitId);

    /// <summary>Tek ünite özet sensörleri — gaz veya petrol anahtarları.</summary>
    public StationSensors SensorsForUnit(string unitId)
    {
        var t = Telem(unitId);
        if (t == null || t.Count == 0) return default;
        return new StationSensors(
            Pick(t, "s_7_vibration_de_mm_s", "s_vibration_mm_s"),
            Pick(t, "s_10_bearing_temp_1_c", "s_bearing_temp_c"),
            Pick(t, "s_2_discharge_pressure_bar", "s_discharge_pressure_bar"));
    }
    public MachineSnapshot UnitSnapshot(string unitId)
    {
        if (!TryAi(unitId, out var ai)) return default; // yerel sağlık türetme yok

        // Sensörler canlı SCADA; health/RUL AI.
        var t = Telem(unitId);
        double vib = t != null && t.TryGetValue("s_7_vibration_de_mm_s", out var lv) ? lv : ai.Vibration;
        double temp = t != null && t.TryGetValue("s_10_bearing_temp_1_c", out var lt) ? lt : ai.BearingTemp;
        double press = t != null && t.TryGetValue("s_2_discharge_pressure_bar", out var lp) ? lp : ai.DischargePressure;

        return new MachineSnapshot(
            Temperature: Math.Round(temp, 1),
            Pressure: Math.Round(press, 2),
            Vibration: Math.Round(vib, 2),
            Rul: Math.Round(ai.Rul, 1),
            HealthScore: Math.Round(ai.Health, 1),
            Anomaly: ai.Health < 20,
            IsDegraded: ai.Health < 40,
            AnomalyProbability: ai.Health < 20 ? 1 : ai.Health < 40 ? 0.5 : 0);
    }

    public SegSnap Segment(string id)
    {
        // Segment flow/load/leak — AI respond (sunucu). Yoksa live flow (sunucu);
        // sızıntı yalnız AI'dan (yerel dengesizlik eşiği yok).
        if (TryAiSeg(id, out var aiSeg))
        {
            double flowMcmDay = aiSeg.FlowM3H * 24.0 / 1e6;
            return new SegSnap(Math.Round(flowMcmDay, 1), aiSeg.Load, aiSeg.Leak);
        }

        var t = Telem(id);
        if (t == null) return new SegSnap(0, 0, false);

        double flowM3H = t.GetValueOrDefault("s_flow_m3_h");
        double flowMcmDayLive = flowM3H * 24.0 / 1e6;
        // load/leak yalnız AI'dan; canlıda oran/eşik hesabı yok
        return new SegSnap(Math.Round(flowMcmDayLive, 1), 0, false);
    }

    public StationSensors Sensors(string id)
    {
        // Titreşim/sıcaklık/basınç = canlı SCADA (AI vib sıkça tüm ünitelerde 9.0).
        static StationSensors FromLive(IReadOnlyDictionary<string, double> t) => new(
            Pick(t, "s_7_vibration_de_mm_s", "s_vibration_mm_s"),
            Pick(t, "s_10_bearing_temp_1_c", "s_bearing_temp_c"),
            Pick(t, "s_2_discharge_pressure_bar", "s_discharge_pressure_bar"));

        var aiW = WorstAiUnit(id);
        if (aiW != null)
        {
            var tWorst = Telem(aiW.Value.UnitId);
            if (tWorst != null && tWorst.Count > 0) return FromLive(tWorst);
        }

        if (!_data.StationUnits.TryGetValue(id, out var units)) return default;
        foreach (var u in units)
        {
            var t = Telem(u.Id);
            if (t == null || t.Count == 0) continue;
            return FromLive(t);
        }
        return default;
    }

    public double Level(string id)
    {
        var t = Telem(id);
        if (t == null) return 0;
        return t.TryGetValue("s_storage_level_pct", out var v) ? v
             : t.GetValueOrDefault("s_tank_level_pct");
    }
}
