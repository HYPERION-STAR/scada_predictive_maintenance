using System.IO;
using System.Text.Json;

namespace ScadaDashboard.Services;

/// <summary>Harita veri kaynağı seçimi (ayarlar penceresinden).</summary>
public enum SourceMode
{
    Otomatik,    // SCADA_HUB_URL > snapshot dosyası > simülasyon
    Simulasyon,
    Snapshot,
    ApiHub,
}

/// <summary>
/// Uygulama ayarları. Exe klasöründeki settings.json'a yazılır/okunur;
/// dosya yoksa varsayılanlar geçerli (Otomatik kaynak seçimi).
/// </summary>
public sealed class AppSettings
{
    public SourceMode SourceMode { get; set; } = SourceMode.Otomatik;
    public string HubUrl { get; set; } = "";

    private static string PathFor() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var p = PathFor();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
        }
        catch { /* bozuk dosya → varsayılanlar */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(PathFor(),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* yazılamazsa ayar yalnızca oturumluk kalır */ }
    }
}
