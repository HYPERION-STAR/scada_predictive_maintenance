using System;
using System.Windows;
using ScadaDashboard.Services;
using ScadaDashboard.ViewModels;

namespace ScadaDashboard;

public partial class MainWindow : Window
{
    // Uyari paneli kucultulmeden onceki yuksekligi (geri acinca kullanilir).
    private GridLength _savedAlertHeight = new(190);
    private bool _alertsMinimized;

    public MainWindow()
    {
        InitializeComponent();

        // Veri kaynagi + uyari servisi kuruldu.
        // Ileride: new SimulatorDataSource() -> new MqttDataSource(...) ile degistirilecek.
        var dataSource = new SimulatorDataSource();
        var alerts = new AlertService();
        DataContext = new MainViewModel(dataSource, alerts);
    }

    // Boru hatti canli haritasini ac.
    private void OpenMap_Click(object sender, RoutedEventArgs e) => new MapWindow().Show();

    // Alt uyari panelini kucult / geri ac.
    private void ToggleAlerts_Click(object sender, RoutedEventArgs e)
    {
        if (_alertsMinimized)
        {
            // Geri ac: kaydedilen yukseklige don, surukleme tekrar aktif.
            AlertList.Visibility = Visibility.Visible;
            AlertSplitter.IsEnabled = true;
            AlertRow.MinHeight = 40;
            AlertRow.MaxHeight = 520;
            AlertRow.Height = _savedAlertHeight;
            AlertToggleIcon.Text = "▾"; // ▾
            _alertsMinimized = false;
        }
        else
        {
            // Kucult: mevcut yuksekligi sakla, sadece baslik gorunur kalsin.
            _savedAlertHeight = new GridLength(Math.Max(90, AlertRow.ActualHeight));
            AlertList.Visibility = Visibility.Collapsed;
            AlertSplitter.IsEnabled = false;
            AlertRow.MinHeight = 0;
            AlertRow.MaxHeight = double.PositiveInfinity;
            AlertRow.Height = GridLength.Auto; // baslik yuksekligine kucul
            AlertToggleIcon.Text = "▴"; // ▴
            _alertsMinimized = true;
        }
    }
}
