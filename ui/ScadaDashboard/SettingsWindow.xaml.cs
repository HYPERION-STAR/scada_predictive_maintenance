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

    public SettingsWindow(AppSettings current, string? snapshotPath)
    {
        InitializeComponent();
        WindowFx.Apply(this);
        Result = current;

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

        // Canlı da topolojiyi snapshot dosyasından alır; dosya yoksa seçilemez.
        LiveInfo.Text = snapshotPath != null
            ? $"Canlı telemetri; topoloji: {System.IO.Path.GetFileName(snapshotPath)}"
            : "Canlı için snapshot topoloji dosyası gerekli (bulunamadı)";
        ModeLive.IsEnabled = snapshotPath != null;
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

        Result = new AppSettings { SourceMode = mode, HubUrl = url, LiveUrl = liveUrl };
        Result.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
