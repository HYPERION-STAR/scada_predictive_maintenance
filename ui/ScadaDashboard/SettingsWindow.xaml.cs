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
        Result = current;

        ModeAuto.IsChecked = current.SourceMode == SourceMode.Otomatik;
        ModeSim.IsChecked = current.SourceMode == SourceMode.Simulasyon;
        ModeSnap.IsChecked = current.SourceMode == SourceMode.Snapshot;
        ModeHub.IsChecked = current.SourceMode == SourceMode.ApiHub;
        HubUrlBox.Text = current.HubUrl;

        SnapInfo.Text = snapshotPath != null
            ? $"Bulunan dosya: {snapshotPath}"
            : "Snapshot dosyası bulunamadı (*live_snapshot.json)";
        ModeSnap.IsEnabled = snapshotPath != null;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var mode = ModeSim.IsChecked == true ? SourceMode.Simulasyon
                 : ModeSnap.IsChecked == true ? SourceMode.Snapshot
                 : ModeHub.IsChecked == true ? SourceMode.ApiHub
                 : SourceMode.Otomatik;

        string url = HubUrlBox.Text.Trim();
        if (mode == SourceMode.ApiHub && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ErrorText.Text = "Geçerli bir hub adresi girin (ör. http://localhost:8000).";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new AppSettings { SourceMode = mode, HubUrl = url };
        Result.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
