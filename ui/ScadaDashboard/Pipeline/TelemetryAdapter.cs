using System.Reflection;
using System.Text.Json.Serialization;
using ScadaClient.Models.Telemetry;

namespace ScadaDashboard.Pipeline;

/// <summary>Adaptörün ürettiği iki paralel harita: sayısal sensör değerleri ve
/// varlık başına telemetri kalitesi (TelemetryBase.Quality — GOOD/BAD/…).
/// İkisi de aynı anahtar uzayını (SnapshotLoader.Norm) kullanır.</summary>
internal readonly record struct SensorMaps(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Values,
    IReadOnlyDictionary<string, string> Quality);

/// <summary>
/// ScadaClient'ın tipli telemetri DTO'larını (CompressorTelemetry vb.) UI'ın
/// türetme katmanının beklediği jenerik sensör sözlüğüne çevirir
/// (ör. "s_7_vibration_de_mm_s" → 5.46). Anahtarlar DTO'lardaki
/// [JsonPropertyName] değerlerinden okunur — böylece SnapshotSource'un
/// dosya-tabanlı yoluyla (SnapshotLoader) aynı anahtar uzayı kullanılır.
/// Her varlığın <see cref="TelemetryBase.Quality"/> alanı da ayrı haritada taşınır
/// (BAD/UNCERTAIN telemetri UI'da işaretlensin; dosya yolunda bu alan yoktur).
/// </summary>
internal static class TelemetryAdapter
{
    // DTO tipi → (json alan adı, double property) erişimcileri (yansıma bir kez).
    private static readonly Dictionary<Type, (string Json, PropertyInfo Prop)[]> _cache = new();

    public static SensorMaps ToSensorMaps(IReadOnlyDictionary<string, TelemetryBase> live)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, double>>();
        var quality = new Dictionary<string, string>();
        foreach (var (key, tel) in live)
        {
            var accessors = Accessors(tel.GetType());
            var vals = new Dictionary<string, double>(accessors.Length);
            foreach (var (json, prop) in accessors)
                vals[json] = (double)prop.GetValue(tel)!;

            // Hem sözlük anahtarı hem entity_id ile eriş (SnapshotLoader.Norm kuralı).
            string k = SnapshotLoader.Norm(key);
            result[k] = vals; quality[k] = tel.Quality;
            if (tel.EntityId.Length > 0)
            {
                string e = SnapshotLoader.Norm(tel.EntityId);
                result[e] = vals; quality[e] = tel.Quality;
            }
        }
        return new SensorMaps(result, quality);
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
