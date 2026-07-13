using System.Windows;
using ScadaDashboard.Services;
using ScadaDashboard.ViewModels;

namespace ScadaDashboard;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Veri kaynagi + uyari servisi kuruldu.
        // Ileride: new SimulatorDataSource() -> new MqttDataSource(...) ile degistirilecek.
        var dataSource = new SimulatorDataSource();
        var alerts = new AlertService();
        DataContext = new MainViewModel(dataSource, alerts);
    }
}
