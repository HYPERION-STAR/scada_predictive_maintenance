using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Gömülü komşu ülke sınırlarını (neighbors.json) yükler — coğrafi bağlam için
/// harita zemininde çizilir. Her ülke, [lon, lat] noktalı poligon halkalarından
/// oluşur. Kaynak: johan/world.geo.json (110m, MIT), sadeleştirilmiş.
/// </summary>
public static class NeighborMap
{
    public sealed class Country
    {
        public string Name { get; init; } = "";
        /// <summary>Halkalar; her halka [lon, lat] çiftlerinden oluşur.</summary>
        public List<double[][]> Rings { get; init; } = new();
    }

    private static List<Country>? _cache;
    private static bool _tried;

    public static IReadOnlyList<Country> Countries
    {
        get
        {
            if (!_tried) { _tried = true; _cache = Load(); }
            return _cache ?? new List<Country>();
        }
    }

    private sealed class Dto { public List<CountryDto> Countries { get; set; } = new(); }
    private sealed class CountryDto
    {
        public string Name { get; set; } = "";
        public List<List<List<double>>> Rings { get; set; } = new();
    }

    private static List<Country>? Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("ScadaDashboard.Pipeline.neighbors.json");
            if (stream == null) return null;

            var dto = JsonSerializer.Deserialize<Dto>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return null;

            var list = new List<Country>(dto.Countries.Count);
            foreach (var c in dto.Countries)
            {
                var rings = new List<double[][]>(c.Rings.Count);
                foreach (var ring in c.Rings)
                {
                    var pts = new double[ring.Count][];
                    for (int i = 0; i < ring.Count; i++)
                        pts[i] = new[] { ring[i][0], ring[i][1] }; // [lon, lat]
                    rings.Add(pts);
                }
                list.Add(new Country { Name = c.Name, Rings = rings });
            }
            return list;
        }
        catch
        {
            return null; // yüklenemezse komşular çizilmez, harita yine çalışır
        }
    }
}
