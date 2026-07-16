using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScadaDashboard.Pipeline;

namespace ScadaDashboard;

/// <summary>Detay panelindeki tek sensör satırı (ad + değer).</summary>
public sealed record SensorRow(string Name, string Value);

public partial class MapWindow : Window
{
    private IPipelineSource _source = null!;   // ApplySource ctor'da doldurur
    private Services.AppSettings _settings = Services.AppSettings.Load();
    private readonly DispatcherTimer _render;
    private int _renderTicks;

    private string? _selected;                         // acik detay istasyonu
    private readonly List<double> _vib = new(), _bt = new(), _dp = new();

    public MapWindow()
    {
        InitializeComponent();

        // Veri kaynagi: kalici ayar (settings.json) uygulanir; "Otomatik" modda
        // oncelik SCADA_HUB_URL > snapshot dosyasi > simulasyon.
        ApplySource(_settings);
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

    // Secilen veri kaynagini kur ve UI'yi guncelle (baslangicta + ayar degisince).
    private void ApplySource(Services.AppSettings s)
    {
        string? snapshotPath = FindSnapshotFile();
        IPipelineSource src;
        string label;

        if (s.SourceMode == Services.SourceMode.Simulasyon)
        {
            PipelineTopology.ResetToDefault();
            src = new PipelineSimulator(); label = "SİMÜLASYON";
        }
        else if (s.SourceMode == Services.SourceMode.Snapshot
                 && snapshotPath != null && TryLoadSnapshot(snapshotPath, out var snap))
        {
            src = snap; label = "SNAPSHOT";
        }
        else if (s.SourceMode == Services.SourceMode.ApiHub && s.HubUrl.Length > 0)
        {
            PipelineTopology.ResetToDefault();
            src = new HttpPipelineSource(s.HubUrl); label = "API HUB";
        }
        else // Otomatik (veya secilen kaynak kurulamadi)
        {
            var hub = Environment.GetEnvironmentVariable("SCADA_HUB_URL");
            if (!string.IsNullOrWhiteSpace(hub))
            {
                PipelineTopology.ResetToDefault();
                src = new HttpPipelineSource(hub); label = "API HUB";
            }
            else if (snapshotPath != null && TryLoadSnapshot(snapshotPath, out var snap2))
            {
                src = snap2; label = "SNAPSHOT";
            }
            else
            {
                PipelineTopology.ResetToDefault();
                src = new PipelineSimulator(); label = "SİMÜLASYON";
            }
        }

        _source = src;
        SourceLabel.Text = $"  •  Veri kaynağı: {label}";
        UnitsButton.Visibility = _source is PipelineSimulator or SnapshotSource
            ? Visibility.Visible : Visibility.Collapsed;
        Map.Source = _source;

        // Acik detay eski kaynaga aitti; kapat.
        _selected = null;
        Detail.Visibility = Visibility.Collapsed;
        UpdateAlarms();
    }

    // Ayarlar penceresi: kaynak degisirse aninda uygula.
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, FindSnapshotFile()) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            ApplySource(_settings);
        }
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

    // Alt bardaki kategori onay kutusu: isaret kalkinca o tur haritada gizlenir.
    private void CategoryToggle(object sender, RoutedEventArgs e)
    {
        if (Map == null) return; // XAML parse sirasinda erken tetiklenme
        if (sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not string cat) return;
        if (cb.IsChecked == true) Map.HiddenCategories.Remove(cat);
        else Map.HiddenCategories.Add(cat);
        Map.InvalidateVisual();
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
            UpdateSensorList(n.Id);
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

    // 21 kompresor sensoru: anahtar -> (Turkce ad, birim). Bilinmeyene jenerik ad.
    private static readonly Dictionary<string, (string Ad, string Birim)> SensorAdlari = new()
    {
        ["s_1_suction_pressure_bar"] = ("Emme Basıncı", "bar"),
        ["s_2_discharge_pressure_bar"] = ("Basma Basıncı", "bar"),
        ["s_3_pressure_ratio"] = ("Basınç Oranı", ""),
        ["s_4_suction_temp_c"] = ("Emme Sıcaklığı", "°C"),
        ["s_5_discharge_temp_c"] = ("Basma Sıcaklığı", "°C"),
        ["s_6_shaft_rpm"] = ("Şaft Devri", "rpm"),
        ["s_7_vibration_de_mm_s"] = ("Titreşim (DE)", "mm/s"),
        ["s_8_vibration_nde_mm_s"] = ("Titreşim (NDE)", "mm/s"),
        ["s_9_axial_displacement_mm"] = ("Eksenel Kayma", "mm"),
        ["s_10_bearing_temp_1_c"] = ("Yatak Sıcaklığı 1", "°C"),
        ["s_11_bearing_temp_2_c"] = ("Yatak Sıcaklığı 2", "°C"),
        ["s_12_lube_oil_pressure_bar"] = ("Yağlama Yağı Basıncı", "bar"),
        ["s_13_lube_oil_temp_c"] = ("Yağlama Yağı Sıcaklığı", "°C"),
        ["s_14_gas_flow_meter_m3_h"] = ("Gaz Akış Sayacı", "m³/sa"),
        ["s_15_seal_gas_pressure_bar"] = ("Sızdırmazlık Gazı Basıncı", "bar"),
        ["s_16_gas_flow_m3_h"] = ("Gaz Akışı", "m³/sa"),
        ["s_17_power_mw"] = ("Güç", "MW"),
        ["s_18_polytropic_efficiency"] = ("Politropik Verim", ""),
        ["s_19_surge_margin_pct"] = ("Surge Marjı", "%"),
        ["s_20_filter_dp_bar"] = ("Filtre ΔP", "bar"),
        ["s_21_torque_nm"] = ("Tork", "Nm"),
    };

    // Detay panelinin sensor listesini doldur (yalnizca snapshot kaynaginda).
    private void UpdateSensorList(string stationId)
    {
        if (_source is SnapshotSource snap && snap.WorstUnitSensors(stationId) is { } wu)
        {
            SensorHeader.Text = $"TÜM SENSÖRLER — {wu.UnitId} (EN KÖTÜ ÜNİTE)";
            SensorList.ItemsSource = wu.Sensors
                .Where(kv => kv.Key.StartsWith("s_"))
                .OrderBy(kv => SensorSira(kv.Key))
                .Select(kv =>
                {
                    var (ad, birim) = SensorAdlari.TryGetValue(kv.Key, out var s)
                        ? s : (GenelSensorAdi(kv.Key), "");
                    string deger = kv.Value.ToString("0.##");
                    return new SensorRow(ad, birim.Length > 0 ? $"{deger} {birim}" : deger);
                })
                .ToList();
            SensorHeader.Visibility = Visibility.Visible;
            SensorList.Visibility = Visibility.Visible;
        }
        else
        {
            SensorHeader.Visibility = Visibility.Collapsed;
            SensorList.Visibility = Visibility.Collapsed;
        }
    }

    // "s_12_..." -> 12 (dosyadaki sensor sirasi korunur).
    private static int SensorSira(string key)
    {
        var parts = key.Split('_');
        return parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 99;
    }

    // Bilinmeyen anahtar icin okunur ad: "s_22_foo_bar" -> "foo bar".
    private static string GenelSensorAdi(string key)
    {
        var parts = key.Split('_');
        return string.Join(' ', parts.Skip(parts.Length > 1 && int.TryParse(parts[1], out _) ? 2 : 1));
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
