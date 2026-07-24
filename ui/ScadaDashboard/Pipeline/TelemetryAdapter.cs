using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScadaClient.Models.Telemetry;

namespace ScadaDashboard.Pipeline;

/// <summary>Adaptörün ürettiği haritalar — değerler, kalite, işletim durumu (pompa status).</summary>
internal readonly record struct SensorMaps(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Values,
    IReadOnlyDictionary<string, string> Quality,
    IReadOnlyDictionary<string, string> Status);

/// <summary>
/// ScadaClient'ın tipli telemetri DTO'larını UI sensör sözlüğüne çevirir.
/// Gaz (s_7_…) ve petrol pompa (s_vibration_mm_s) alanları aynı haritada durur.
/// </summary>
internal static class TelemetryAdapter
{
    private static readonly Dictionary<Type, (string Json, PropertyInfo Prop)[]> _cache = new();

    public static SensorMaps ToSensorMaps(IReadOnlyDictionary<string, TelemetryBase> live)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, double>>();
        var quality = new Dictionary<string, string>();
        var status = new Dictionary<string, string>();
        foreach (var (key, tel) in live)
        {
            var accessors = Accessors(tel.GetType());
            var vals = new Dictionary<string, double>(accessors.Length);
            foreach (var (json, prop) in accessors)
                vals[json] = (double)prop.GetValue(tel)!;

            if (tel.Extra is { Count: > 0 } extra)
            {
                foreach (var (name, je) in extra)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d))
                        vals.TryAdd(name, d);
                }
            }

            string st = tel is OilPumpTelemetry op ? op.Status : "";
            if (st.Length == 0 && tel.Extra != null
                && tel.Extra.TryGetValue("status", out var sj)
                && sj.ValueKind == JsonValueKind.String)
                st = sj.GetString() ?? "";

            string k = SnapshotLoader.Norm(key);
            result[k] = vals;
            quality[k] = tel.Quality;
            if (st.Length > 0) status[k] = st;

            if (tel.EntityId.Length > 0)
            {
                string e = SnapshotLoader.Norm(tel.EntityId);
                result[e] = vals;
                quality[e] = tel.Quality;
                if (st.Length > 0) status[e] = st;
            }
        }
        return new SensorMaps(result, quality, status);
    }

    private static (string Json, PropertyInfo Prop)[] Accessors(Type type)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(type, out var cached)) return cached;
            var list = new List<(string, PropertyInfo)>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(double)) continue;
                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (name != null) list.Add((name, p));
            }
            return _cache[type] = list.ToArray();
        }
    }
}
