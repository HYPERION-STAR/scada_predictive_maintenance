using System.IO;
using System.Text.Json;

namespace ScadaDashboard.Services;

/// <summary>Harita veri kaynağı seçimi (ayarlar penceresinden).</summary>
public enum SourceMode
{
    Otomatik,    // SCADA_LIVE_URL > SCADA_HUB_URL > snapshot dosyası > simülasyon
    Simulasyon,
    Snapshot,
    ApiHub,
    Canli,       // canlı telemetri endpoint'i (snapshot topolojisi + HTTP yoklama)
}

/// <summary>
/// Uygulama ayarları. Exe klasöründeki settings.json'a yazılır/okunur;
/// dosya yoksa varsayılanlar geçerli (Otomatik kaynak seçimi).
/// </summary>
public sealed class AppSettings
{
    public SourceMode SourceMode { get; set; } = SourceMode.Otomatik;
    public string HubUrl { get; set; } = "";

    /// <summary>Açık tema (varsayılan koyu).</summary>
    public bool LightTheme { get; set; } = false;

    /// <summary>Canlı telemetri endpoint'i (Canli modu; topoloji yine snapshot dosyasından).</summary>
    public string LiveUrl { get; set; } = "http://100.114.223.5:8000/api/scada/live_data";

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
