using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScadaDashboard.Pipeline;

namespace ScadaDashboard;

public partial class MapWindow : Window
{
    private readonly IPipelineSource _source;
    private readonly DispatcherTimer _render;
    private int _renderTicks;

    private string? _selected;                         // acik detay istasyonu
    private readonly List<double> _vib = new(), _bt = new(), _dp = new();

    public MapWindow()
    {
        InitializeComponent();

        // Veri kaynagi onceligi: SCADA_HUB_URL (API Hub) > snapshot dosyasi >
        // simulasyon. Boylece backend olmadan da UI bagimsiz calisir.
        var hub = Environment.GetEnvironmentVariable("SCADA_HUB_URL");
        string? snapshotPath = string.IsNullOrWhiteSpace(hub) ? FindSnapshotFile() : null;
        if (!string.IsNullOrWhiteSpace(hub))
        {
            _source = new HttpPipelineSource(hub);
            SourceLabel.Text = "  •  Veri kaynağı: API HUB";
        }
        else if (snapshotPath != null && TryLoadSnapshot(snapshotPath, out var snapSource))
        {
            _source = snapSource;
            SourceLabel.Text = "  •  Veri kaynağı: SNAPSHOT";
        }
        else
        {
            _source = new PipelineSimulator();
        }

        // Uniteler simulasyon ve snapshot kaynaklarinda var (Hub'da ileride).
        UnitsButton.Visibility = _source is PipelineSimulator or SnapshotSource
            ? Visibility.Visible : Visibility.Collapsed;

        Map.Source = _source;
        Map.NodeClicked += OnNodeClicked;

        // ~16 fps render (akis animasyonu); simulasyon durumu saniyede 1 ilerler.
        _render = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _render.Tick += (_, _) =>
        {
            _renderTicks++;
            if (_renderTicks % 16 == 0)           // ~ her 1 sn
            {
                _source.Tick();
                Clock.Text = DateTime.Now.ToString("dd.MM.yyyy  HH:mm:ss");
                UpdateAlarms();
                if (_selected != null) UpdateDetail();
            }
            if (AlarmBox.Visibility == Visibility.Visible)   // yanip sonme
                AlarmBox.Opacity = 0.4 + 0.6 * (0.5 + 0.5 * Math.Sin(Environment.TickCount / 250.0));
            Map.InvalidateVisual();
        };
        UpdateAlarms();
        _render.Start();
    }

    // Tam ag snapshot dosyasini bul: SCADA_SNAPSHOT env yolu, yoksa exe
    // klasorunden yukari dogru "*live_snapshot.json" aranir (dosya repo
    // kokunde, git disi tutulur).
    private static string? FindSnapshotFile()
    {
        var env = Environment.GetEnvironmentVariable("SCADA_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(env) && System.IO.File.Exists(env)) return env;

        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var hits = dir.GetFiles("*live_snapshot.json");
            if (hits.Length > 0) return hits[0].FullName;
        }
        return null;
    }

    // Snapshot'i yukle; topolojiyi tam agla degistir. Bozuk dosyada sessizce
    // simulasyona dusulur (harita her kosulda calisir).
    private static bool TryLoadSnapshot(string path, out SnapshotSource source)
    {
        try
        {
            var data = SnapshotLoader.Load(path);
            if (data.Nodes.Count == 0) { source = null!; return false; }
            PipelineTopology.Load(data.Nodes, data.Segments);
            source = new SnapshotSource(data);
            return true;
        }
        catch
        {
            source = null!;
            return false;
        }
    }

    // Genel makine panelini ac (5 jenerik makine).
    private void OpenDashboard_Click(object sender, RoutedEventArgs e) => new MainWindow().Show();

    // Kritik istasyonlari say, ust bar alarm rozetini guncelle.
    private void UpdateAlarms()
    {
        int crit = 0;
        foreach (var n in PipelineTopology.Nodes)
            if (n.IsStation && _source.Node(n.Id).Health < 20) crit++;
        if (crit > 0)
        {
            AlarmText.Text = $"⚠  {crit} KRİTİK İSTASYON";
            AlarmBox.Visibility = Visibility.Visible;
        }
        else AlarmBox.Visibility = Visibility.Collapsed;
    }

    private void OnNodeClicked(PNode n)
    {
        if (n.IsStation)
        {
            _selected = n.Id;
            _vib.Clear(); _bt.Clear(); _dp.Clear();
            DetailId.Text = n.Id;
            DetailName.Text = n.Name;
            Detail.Visibility = Visibility.Visible;
            UpdateDetail();
            SelectedInfo.Text = $"{n.Id}  {n.Name}";
        }
        else
        {
            SelectedInfo.Text = $"{n.Id}  {n.Name}  —  {n.Type} (izleme dışı)";
        }
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        Detail.Visibility = Visibility.Collapsed;
    }

    // Secili istasyonun unitelerini makine karti panelinde ac.
    private void OpenUnits_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Services.IDataSource? units = _source switch
        {
            PipelineSimulator sim => new StationDataSource(sim, _selected),
            SnapshotSource snap => new SnapshotStationSource(snap, _selected),
            _ => null,
        };
        if (units != null)
            new MainWindow(units, $"{DetailName.Text} - Üniteler") { Owner = this }.Show();
    }

    private void UpdateDetail()
    {
        if (_selected == null) return;
        var snap = _source.Node(_selected);
        var s = _source.Sensors(_selected);
        Push(_vib, s.Vibration); Push(_bt, s.BearingTemp); Push(_dp, s.DischargePressure);
        VibChart.Values = _vib.ToArray(); BtChart.Values = _bt.ToArray(); DpChart.Values = _dp.ToArray();
        VibVal.Text = $"{s.Vibration:0.00} mm/s";
        BtVal.Text = $"{s.BearingTemp:0.0} °C";
        DpVal.Text = $"{s.DischargePressure:0.00} bar";
        DetailRul.Text = $"{snap.Rul:0} döngü";
        DetailHealth.Text = $"%{snap.Health:0}";
        string durum = snap.Health >= 70 ? "SAĞLIKLI" : snap.Health >= 40 ? "UYARI"
                     : snap.Health >= 20 ? "RİSKLİ" : "KRİTİK";
        DetailStatus.Text = durum;
        DetailStatusBox.Background = new SolidColorBrush(
            snap.Health >= 70 ? Color.FromRgb(0x2E, 0xCC, 0x71)
          : snap.Health >= 40 ? Color.FromRgb(0xF1, 0xC4, 0x0F)
          : snap.Health >= 20 ? Color.FromRgb(0xE6, 0x7E, 0x22)
          : Color.FromRgb(0xE7, 0x4C, 0x3C));
    }

    private static void Push(List<double> buf, double v)
    {
        buf.Add(v);
        if (buf.Count > 60) buf.RemoveAt(0);
    }

    protected override void OnClosed(EventArgs e)
    {
        _render.Stop();
        base.OnClosed(e);
    }
}
