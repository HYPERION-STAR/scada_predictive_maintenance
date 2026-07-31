using System.IO;
using System.Text.Json;

namespace ScadaDashboard.Services;

/// <summary>
/// Uygulama ayarları. Exe klasöründeki settings.json'a yazılır/okunur.
/// Gaz: canlı API. Petrol: MySQL live_entity_current (erişilemezse canlıya düşer).
/// Sağlık/RUL: AI respond overlay.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Canlı telemetri endpoint'i (topoloji + gaz sensors sunucudan).</summary>
    public string LiveUrl { get; set; } = "http://100.114.223.5:8000/api/scada/live_data";

    /// <summary>
    /// AI/DB uç noktası — health/rul ve hub alanları (zorunlu; yerel proxy yok).
    /// </summary>
    public string AiRespondUrl { get; set; } = "http://100.96.102.16:9000";

    /// <summary>
    /// MySQL bağlantısı — petrol (oil_pump / oil_storage) <c>live_entity_current</c>'tan.
    /// Boş bırakılırsa petrol de canlı API'den gelir.
    /// </summary>
    public string DatabaseConnectionString { get; set; } =
        "Server=100.96.102.16;Port=3306;Database=pipeline_digital_twin;User ID=root;Password=SCaDa1974.;SslMode=Preferred";

    /// <summary>Açık tema (varsayılan koyu).</summary>
    public bool LightTheme { get; set; } = false;

    /// <summary>Canlı yoklama aralığı (saniye).</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>HTTP zaman aşımı (saniye).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>İstasyon dairesi görünümü: pasta / ortalama / en kötü.</summary>
    public Pipeline.HealthDisplayMode HealthDisplayMode { get; set; } = Pipeline.HealthDisplayMode.Pie;

    /// <summary>Pasta kenar rengi özeti: en kötü / ortalama.</summary>
    public Pipeline.HealthAggregate HealthOutlineAggregate { get; set; } = Pipeline.HealthAggregate.Worst;

    /// <summary>Kritik alarm eşiği (sağlık %).</summary>
    public double CriticalHealthThreshold { get; set; } = 20;

    /// <summary>Pasta dilim ayırıcı rengi (#RRGGBB). Boş = tema varsayılanı.</summary>
    public string PieSeparatorColorHex { get; set; } = "";

    // Eski settings.json alanları (yok sayılır; deserialize kırılmasın diye).
    public string? SourceMode { get; set; }
    public string? HubUrl { get; set; }

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
            // Eski alanları yazma — yalnız güncel sözleşme.
            var dto = new
            {
                LiveUrl,
                AiRespondUrl,
                DatabaseConnectionString,
                LightTheme,
                PollIntervalSeconds,
                TimeoutSeconds,
                HealthDisplayMode,
                HealthOutlineAggregate,
                CriticalHealthThreshold,
                PieSeparatorColorHex,
            };
            File.WriteAllText(PathFor(),
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* yazılamazsa ayar yalnızca oturumluk kalır */ }
    }
}
