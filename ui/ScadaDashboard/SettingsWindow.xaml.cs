using System.Windows;
using ScadaDashboard.Services;

namespace ScadaDashboard;

/// <summary>
/// Ayarlar penceresi: veri kaynağı seçimi. "Uygula" ile ayar kalıcı yazılır
/// (settings.json) ve DialogResult=true döner; MapWindow kaynağı anında değiştirir.
/// </summary>
public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly bool _origLight; // Vazgeç'te temayi geri almak icin

    public SettingsWindow(AppSettings current, string? snapshotPath)
    {
        InitializeComponent();
        WindowFx.Apply(this);
        Result = current;
        _origLight = current.LightTheme;
        LightThemeBox.IsChecked = current.LightTheme;

        ModeAuto.IsChecked = current.SourceMode == SourceMode.Otomatik;
        ModeSim.IsChecked = current.SourceMode == SourceMode.Simulasyon;
        ModeSnap.IsChecked = current.SourceMode == SourceMode.Snapshot;
        ModeLive.IsChecked = current.SourceMode == SourceMode.Canli;
        ModeHub.IsChecked = current.SourceMode == SourceMode.ApiHub;
        HubUrlBox.Text = current.HubUrl;
        LiveUrlBox.Text = current.LiveUrl;

        SnapInfo.Text = snapshotPath != null
            ? $"Bulunan dosya: {snapshotPath}"
            : "Snapshot dosyası bulunamadı (*live_snapshot.json)";
        ModeSnap.IsEnabled = snapshotPath != null;

        // Canlı: topoloji API'den (/nodes + /segments), telemetri /live_data'dan.
        // API erişilemezse snapshot dosyasına düşer; her koşulda seçilebilir.
        LiveInfo.Text = "Telemetri + topoloji canlı API'den (/nodes, /segments, /live_data)";
        ModeLive.IsEnabled = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var mode = ModeSim.IsChecked == true ? SourceMode.Simulasyon
                 : ModeSnap.IsChecked == true ? SourceMode.Snapshot
                 : ModeLive.IsChecked == true ? SourceMode.Canli
                 : ModeHub.IsChecked == true ? SourceMode.ApiHub
                 : SourceMode.Otomatik;

        string url = HubUrlBox.Text.Trim();
        string liveUrl = LiveUrlBox.Text.Trim();
        if (mode == SourceMode.ApiHub && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ErrorText.Text = "Geçerli bir hub adresi girin (ör. http://localhost:8000).";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (mode == SourceMode.Canli && !Uri.TryCreate(liveUrl, UriKind.Absolute, out _))
        {
            ErrorText.Text = "Geçerli bir canlı adres girin (ör. http://host:8000/api/scada/live_data).";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new AppSettings
        {
            SourceMode = mode, HubUrl = url, LiveUrl = liveUrl,
            LightTheme = LightThemeBox.IsChecked == true,
        };
        Result.Save();
        DialogResult = true;
    }

    // Canli onizleme: onay kutusu degisince temayi hemen uygula.
    private void Theme_Changed(object sender, RoutedEventArgs e)
        => ThemeManager.Apply(LightThemeBox.IsChecked == true ? ThemeMode.Acik : ThemeMode.Koyu);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Onizlenen tema geri alinir.
        ThemeManager.Apply(_origLight ? ThemeMode.Acik : ThemeMode.Koyu);
        DialogResult = false;
    }
}
