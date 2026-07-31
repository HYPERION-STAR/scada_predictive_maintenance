using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScadaDashboard.Pipeline;
using ScadaDashboard.Services;

namespace ScadaDashboard;

/// <summary>
/// Detay panelindeki tek sensör satırı. IsHeader=true grup başlığıdır;
/// Alert 0=normal, 1=uyarı, 2=kritik (değer rengi buna göre).
/// </summary>
public sealed record SensorRow(string Name, string Value, bool IsHeader = false, int Alert = 0);

/// <summary>İstasyon detayında ünite performans kartı (gaz veya petrol).</summary>
public sealed class UnitPerfRow
{
    public string UnitId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public Brush StatusBrush { get; init; } = Brushes.Gray;
    public Brush HealthDot { get; init; } = Brushes.Gray;
    public string HealthText { get; init; } = "—";
    public string RulText { get; init; } = "—";
    public string VibText { get; init; } = "—";
    public string TempText { get; init; } = "—";
    public string PressText { get; init; } = "—";
    public string FlowText { get; init; } = "—";
    public string PowerText { get; init; } = "—";
    public string SuctionText { get; init; } = "—";
    public Visibility GasMetricsVisibility { get; init; } = Visibility.Visible;
    public Visibility OilMetricsVisibility { get; init; } = Visibility.Collapsed;
}

public partial class MapWindow : Window
{
    private IPipelineSource _source = null!;   // ApplySource ctor'da doldurur
    private Services.AppSettings _settings = Services.AppSettings.Load();
    private readonly DispatcherTimer _render;
    private int _renderTicks;
    private readonly AlertService _alerts = new();
    private readonly HashSet<string> _ackedAlarms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedAlarms = new(StringComparer.OrdinalIgnoreCase);

    private string? _selected;                         // acik detay istasyonu
    private string? _selectedUnit;                     // ünite performansı seçimi
    private bool _unitPerfUpdating;                    // ListBox yenilemede SelectionChanged bastır
    private readonly List<double> _vib = new(), _bt = new(), _dp = new();
    public MapWindow()
    {
        InitializeComponent();
        WindowFx.Apply(this); // koyu baslik cubugu + yuvarlak kose + belirme

        // Veri kaynağı: yalnız canlı sunucular (snapshot / simülasyon yok).
        ApplySource(_settings);
        InitHealthViewControls();
        Map.NodeClicked += OnNodeClicked;
        ThemeManager.Changed += OnThemeChanged; // tema degisince harita yeniden cizilir

        // ~16 fps render (akis animasyonu); simulasyon durumu saniyede 1 ilerler.
        _render = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _render.Tick += (_, _) =>
        {
            _renderTicks++;
            if (_renderTicks % 16 == 0)           // ~ her 1 sn
            {
                _source.Tick();
                Clock.Text = DateTime.Now.ToString("dd.MM.yyyy  HH:mm:ss");
                UpdateAiStatus();
                UpdateAlarms();
                // Gaz + petrol pompa panelleri periyodik tazelenir.
                if (_selected != null && PipelineTopology.NodeById(_selected) is { } sel
                    && (sel.IsStation || sel.Type is "PS" or "PT"))
                {
                    UpdateDetail();
                    UpdateSensorList(_selected);
                }            }
            if (AlarmButton.Visibility == Visibility.Visible && AlarmButton.IsChecked != true)
                AlarmBox.Opacity = 0.4 + 0.6 * (0.5 + 0.5 * Math.Sin(Environment.TickCount / 250.0));
            else
                AlarmBox.Opacity = 1;
            Map.InvalidateVisual();
        };
        UpdateAiStatus();
        UpdateAlarms();
        _render.Start();
    }

    // Yalnız canlı API (+ AI overlay). Snapshot / simülasyon / hub yok.
    private void ApplySource(Services.AppSettings s)
    {
        IPipelineSource src;
        string label;

        if (s.LiveUrl.Length > 0 && !string.IsNullOrWhiteSpace(s.AiRespondUrl)
            && TryLoadApiLive(s.LiveUrl, s, out var live))
        {
            src = live;
            label = "CANLI + AI";
        }
        else
        {
            // Sunucu yok — boş topoloji; harita çökmesin diye boş SnapshotSource.
            PipelineTopology.Load(Array.Empty<PNode>(), Array.Empty<PSegment>());
            src = new SnapshotSource(new SnapshotData
            {
                Nodes = Array.Empty<PNode>(),
                Segments = Array.Empty<PSegment>(),
                Telemetry = new Dictionary<string, IReadOnlyDictionary<string, double>>(),
                StationUnits = new Dictionary<string, IReadOnlyList<SnapshotUnit>>(),
            });
            label = "SUNUCU YOK";
        }

        if (!ReferenceEquals(_source, src) && _source is IDisposable old) old.Dispose();

        _source = src;
        SourceLabel.Text = $"  •  Veri kaynağı: {label}";
        Map.Source = _source;
        Map.HealthMode = _settings.HealthDisplayMode;
        Map.OutlineAggregate = _settings.HealthOutlineAggregate;
        Map.CriticalThreshold = _settings.CriticalHealthThreshold;
        Map.SetPieSeparatorColorHex(_settings.PieSeparatorColorHex);
        BuildRegionBar();

        _ackedAlarms.Clear();
        _loggedAlarms.Clear();
        AlarmButton.IsChecked = false;
        _selected = null;
        _selectedUnit = null;
        Map.SelectedId = null;
        Detail.Visibility = Visibility.Collapsed;
        UpdateAiStatus();
        UpdateAlarms();
        RefreshPieSepSwatch();
    }

    // Ayarlar penceresi: adresler değişirse anında uygula.
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            ApplySource(_settings);
            UpdateAlarms();
            if (_selected != null && PipelineTopology.NodeById(_selected) is { Type: "CS" })
                RenderStationHealth(_source.Node(_selected), _source.UnitHealths(_selected));
        }
    }

    // Üst bardaki Yenile: topoloji + live + AI sunucudan yeniden.
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ApplySource(_settings);
        UpdateAlarms();
        if (_selected != null && PipelineTopology.NodeById(_selected) is { } n
            && (n.IsStation || n.Type is "PS" or "PT"))
        {
            UpdateDetail();
            UpdateSensorList(_selected);
            if (n.Type == "CS") ShowAiHealthEvaluation(_selected);
        }
    }

    // --- Saglik gorunumu (ust bar menusu) -----------------------------------

    private void InitHealthViewControls()
    {
        HvPie.IsChecked = _settings.HealthDisplayMode == HealthDisplayMode.Pie;
        HvAvg.IsChecked = _settings.HealthDisplayMode == HealthDisplayMode.Average;
        HvWorst.IsChecked = _settings.HealthDisplayMode == HealthDisplayMode.Worst;
        OaWorst.IsChecked = _settings.HealthOutlineAggregate == HealthAggregate.Worst;
        OaAvg.IsChecked = _settings.HealthOutlineAggregate == HealthAggregate.Average;
        UpdateOutlineSectionState();
        RefreshPieSepSwatch();
    }

    private void RefreshPieSepSwatch()
    {
        if (PieSepSwatch == null) return;
        var c = Map.EffectivePieSeparatorColor;
        PieSepSwatch.Background = new SolidColorBrush(c);
        bool custom = !string.IsNullOrWhiteSpace(_settings.PieSeparatorColorHex);
        PieSepHexText.Text = custom ? ColorUtil.ToHex(c) : $"Tema ({ColorUtil.ToHex(c)})";
    }

    private void PickPieSepColor_Click(object sender, RoutedEventArgs e)
    {
        var cur = Map.EffectivePieSeparatorColor;
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
            FullOpen = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var picked = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        _settings.PieSeparatorColorHex = ColorUtil.ToHex(picked);
        Map.SetPieSeparatorColorHex(_settings.PieSeparatorColorHex);
        _settings.Save();
        RefreshPieSepSwatch();
    }

    private void ResetPieSepColor_Click(object sender, RoutedEventArgs e)
    {
        _settings.PieSeparatorColorHex = "";
        Map.SetPieSeparatorColorHex(null);
        _settings.Save();
        RefreshPieSepSwatch();
    }

    private void UpdateOutlineSectionState()
    {
        // Kenar rengi yalniz pasta modunda anlamli.
        if (OutlineSection != null)
            OutlineSection.IsEnabled = _settings.HealthDisplayMode == HealthDisplayMode.Pie;
    }

    private void HealthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null) return; // XAML ayristirma sirasinda erken cagri korumasi
        var mode = HvAvg.IsChecked == true ? HealthDisplayMode.Average
                 : HvWorst.IsChecked == true ? HealthDisplayMode.Worst
                 : HealthDisplayMode.Pie;
        _settings.HealthDisplayMode = mode;
        Map.HealthMode = mode;
        UpdateOutlineSectionState();
        _settings.Save();
        if (_selected != null && PipelineTopology.NodeById(_selected) is { IsStation: true })
            RenderStationHealth(_source.Node(_selected), _source.UnitHealths(_selected));
    }

    private void OutlineAgg_Changed(object sender, RoutedEventArgs e)
    {
        if (Map == null) return;
        var agg = OaAvg.IsChecked == true ? HealthAggregate.Average : HealthAggregate.Worst;
        _settings.HealthOutlineAggregate = agg;
        Map.OutlineAggregate = agg;
        _settings.Save();
        if (_selected != null && PipelineTopology.NodeById(_selected) is { IsStation: true })
            RenderStationHealth(_source.Node(_selected), _source.UnitHealths(_selected));
    }

    // Gorunum moduna gore gosterilecek ozet saglik (baslik + durum kutusu).
    private double ShownHealth(IReadOnlyList<UnitHealth> units, double worstFallback) =>
        _settings.HealthDisplayMode == HealthDisplayMode.Average
            ? HealthAgg.Combine(units, HealthAggregate.Average, worstFallback)
            : _settings.HealthDisplayMode == HealthDisplayMode.Pie
                ? HealthAgg.Combine(units, _settings.HealthOutlineAggregate, worstFallback)
                : worstFallback;

    private void RenderStationHealth(NodeSnap snap, IReadOnlyList<UnitHealth> units)
    {
        double shown = ShownHealth(units, snap.Health);
        DetailRul.Text = $"{snap.Rul:0} döngü";
        DetailHealth.Text = $"%{shown:0}";
        // "KRİTİK" sınırı üst bar alarm rozetiyle aynı eşiği (CriticalHealthThreshold)
        // kullanır; eşik değişince panel ile rozet tutarlı kalır (aksi halde rozet
        // "KRİTİK" sayarken panel "RİSKLİ" gösterebiliyordu).
        bool kritik = shown < _settings.CriticalHealthThreshold;
        string durum = kritik ? "KRİTİK"
                     : shown >= 70 ? "SAĞLIKLI"
                     : shown >= 40 ? "UYARI" : "RİSKLİ";
        DetailStatus.Text = durum;
        DetailStatusBox.Background = new SolidColorBrush(
            kritik ? Color.FromRgb(0xE7, 0x4C, 0x3C) : HealthColorCs(shown));
        RefreshUnitPerfPanel();
    }

    private void UnitPerfToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        RefreshUnitPerfPanel();
        UpdateDetail();
        UpdateSensorList(_selected);
    }

    private void UnitPerfList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_unitPerfUpdating || _selected == null) return;
        if (UnitPerfList.SelectedItem is UnitPerfRow row)
        {
            if (string.Equals(_selectedUnit, row.UnitId, StringComparison.OrdinalIgnoreCase))
                return;
            _selectedUnit = row.UnitId;
            _vib.Clear(); _bt.Clear(); _dp.Clear();
            UpdateDetail();
            UpdateSensorList(_selected);
        }
    }

    private void RefreshUnitPerfPanel()
    {
        if (UnitPerfPanel == null || UnitPerfToggle == null) return;

        if (_selected == null || UnitPerfToggle.IsChecked != true
            || _source is not SnapshotSource snap)
        {
            UnitPerfPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var perfs = snap.UnitPerformance(_selected);
        if (perfs.Count == 0)
        {
            UnitPerfPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Seçim yoksa veya listede yoksa: en kötü AI ünitesi / çalışan pompa / ilk ünite.
        if (_selectedUnit == null
            || !perfs.Any(p => p.UnitId.Equals(_selectedUnit, StringComparison.OrdinalIgnoreCase)))
        {
            var worst = snap.WorstAiUnit(_selected);
            if (worst != null)
                _selectedUnit = worst.Value.UnitId;
            else
            {
                var running = perfs.FirstOrDefault(p =>
                    p.OpStatus.Equals("running", StringComparison.OrdinalIgnoreCase));
                _selectedUnit = !string.IsNullOrEmpty(running.UnitId)
                    ? running.UnitId
                    : perfs[0].UnitId;
            }
        }

        var rows = perfs.Select(ToUnitPerfRow).ToList();
        _unitPerfUpdating = true;
        try
        {
            UnitPerfList.ItemsSource = rows;
            UnitPerfList.SelectedItem = rows.FirstOrDefault(r =>
                r.UnitId.Equals(_selectedUnit, StringComparison.OrdinalIgnoreCase));
        }
        finally { _unitPerfUpdating = false; }

        UnitPerfPanel.Visibility = Visibility.Visible;
    }

    private UnitPerfRow ToUnitPerfRow(UnitPerf p)
    {
        string shortId = ShortUnitLabel(p.UnitId);
        string status;
        Brush statusBrush;
        Color dot;

        if (p.IsOilPump)
        {
            string op = p.OpStatus.Length > 0 ? p.OpStatus : (p.HasLive ? "?" : "veri yok");
            bool running = op.Equals("running", StringComparison.OrdinalIgnoreCase);
            status = op.Equals("running", StringComparison.OrdinalIgnoreCase) ? "Çalışıyor"
                   : op.Equals("standby", StringComparison.OrdinalIgnoreCase) ? "Yedek"
                   : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(op);
            statusBrush = new SolidColorBrush(running
                ? Color.FromRgb(0x2E, 0xCC, 0x71)
                : Color.FromRgb(0x8C, 0xA3, 0xB8));
            dot = Color.FromRgb(0x5B, 0x6B, 0x7C);

            return new UnitPerfRow
            {
                UnitId = p.UnitId,
                Title = shortId,
                Status = status,
                StatusBrush = statusBrush,
                HealthDot = new SolidColorBrush(dot),
                HealthText = "—",
                VibText = p.HasLive ? $"{p.Vibration:0.00}" : "—",
                TempText = p.HasLive ? $"{p.BearingTemp:0.0}°" : "—",
                PressText = p.HasLive ? $"{p.DischargePressure:0.0}" : "—",
                FlowText = p.HasLive ? $"{p.FlowM3H:0}" : "—",
                PowerText = p.HasLive ? $"{p.PumpPowerMw:0.00}" : "—",
                SuctionText = p.HasLive ? $"{p.SuctionPressure:0.0}" : "—",
                GasMetricsVisibility = Visibility.Collapsed,
                OilMetricsVisibility = Visibility.Visible,
            };
        }

        if (!p.HasAi)
        {
            status = "AI yok";
            statusBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7C));
        }
        else if (p.Health < _settings.CriticalHealthThreshold)
        {
            status = "Kritik";
            statusBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        }
        else if (p.Health < 40)
        {
            status = "Riskli";
            statusBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else if (p.Health < 70)
        {
            status = "Uyarı";
            statusBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F));
        }
        else
        {
            status = "Sağlıklı";
            statusBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
        }

        dot = p.HasAi ? HealthColorCs(p.Health) : Color.FromRgb(0x5B, 0x6B, 0x7C);
        return new UnitPerfRow
        {
            UnitId = p.UnitId,
            Title = shortId,
            Status = status,
            StatusBrush = statusBrush,
            HealthDot = new SolidColorBrush(dot),
            HealthText = p.HasAi ? $"%{p.Health:0.#}" : "—",
            RulText = p.HasAi ? $"{p.Rul:0}" : "—",
            VibText = p.HasLive ? $"{p.Vibration:0.00}" : "—",
            TempText = p.HasLive ? $"{p.BearingTemp:0.0}°" : "—",
            PressText = p.HasLive ? $"{p.DischargePressure:0.00}" : "—",
            GasMetricsVisibility = Visibility.Visible,
            OilMetricsVisibility = Visibility.Collapsed,
        };
    }

    private static string ShortUnitLabel(string unitId)
    {
        int u = unitId.LastIndexOf("-U", StringComparison.OrdinalIgnoreCase);
        if (u >= 0 && u + 2 < unitId.Length) return unitId[(u + 1)..]; // "U1"
        int p = unitId.LastIndexOf("-P", StringComparison.OrdinalIgnoreCase);
        if (p >= 0 && p + 2 < unitId.Length) return unitId[(p + 1)..]; // "P1"
        int dash = unitId.LastIndexOf('-');
        return dash >= 0 && dash + 1 < unitId.Length ? unitId[(dash + 1)..] : unitId;
    }

    private static Color HealthColorCs(double h) =>
        h >= 70 ? Color.FromRgb(0x2E, 0xCC, 0x71)
      : h >= 40 ? Color.FromRgb(0xF1, 0xC4, 0x0F)
      : h >= 20 ? Color.FromRgb(0xE6, 0x7E, 0x22)
      : Color.FromRgb(0xE7, 0x4C, 0x3C);

    // Canlı kaynak: topoloji /segments (+ /nodes varsa), gaz telemetri /live_data,
    // petrol telemetri MySQL live_entity_current (yoksa canlı), sağlık AI respond.
    private static bool TryLoadApiLive(string liveUrl, Services.AppSettings s, out LiveSnapshotSource source)
    {
        try
        {
            var baseUrl = new Uri(liveUrl).GetLeftPart(UriPartial.Authority);
            var data = Task.Run(() => ApiSnapshotLoader.Load(baseUrl)).GetAwaiter().GetResult();
            if (data.Nodes.Count == 0) { source = null!; return false; }
            PipelineTopology.Load(data.Nodes, data.Segments);
            source = new LiveSnapshotSource(
                data, baseUrl, s.PollIntervalSeconds, s.TimeoutSeconds, s.AiRespondUrl,
                s.DatabaseConnectionString);
            return true;
        }
        catch
        {
            source = null!;
            return false;
        }
    }

    // Yuzen bolge cubugunu doldur: "Tümü" + her snapshot bolgesi icin cip.
    // Bolge bilgisi yoksa (sim/hub) cubuk gizlenir.
    private void BuildRegionBar()
    {
        RegionBar.Children.Clear();
        var present = new HashSet<string>(PipelineTopology.Nodes.Select(n => n.Region));
        present.Remove("");
        if (present.Count == 0) { RegionBarHost.Visibility = Visibility.Collapsed; return; }

        RegionBarHost.Visibility = Visibility.Visible;
        RegionBar.Children.Add(MakeRegionChip("Tümü", null));
        foreach (var (key, ad) in Pipeline.PipelineMapControl.Regions)
            if (present.Contains(key))
                RegionBar.Children.Add(MakeRegionChip(ad, key));
    }

    private System.Windows.Controls.Button MakeRegionChip(string text, string? regionKey)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            Style = (System.Windows.Style)FindResource("GhostButton"),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(2, 0, 2, 0),
            FontSize = 11,
        };
        b.Click += (_, _) =>
        {
            if (regionKey == null) Map.ResetView();
            else Map.FocusRegion(regionKey);
        };
        return b;
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

    // Katman gorunurluk onay kutulari (ozellestirme).
    private void LayerProvinces(object sender, RoutedEventArgs e)
    { if (Map != null && sender is System.Windows.Controls.CheckBox c) Map.ShowProvinceBorders = c.IsChecked == true; }
    private void LayerGeo(object sender, RoutedEventArgs e)
    { if (Map != null && sender is System.Windows.Controls.CheckBox c) Map.ShowGeoLabels = c.IsChecked == true; }
    private void LayerFlow(object sender, RoutedEventArgs e)
    { if (Map != null && sender is System.Windows.Controls.CheckBox c) Map.ShowFlowArrows = c.IsChecked == true; }
    private void LayerGrid(object sender, RoutedEventArgs e)
    { if (Map != null && sender is System.Windows.Controls.CheckBox c) Map.ShowGrid = c.IsChecked == true; }

    // AI sarmalayıcı özeti.
    private void UpdateAiStatus()
    {
        if (_source is LiveSnapshotSource { AiStatus: { } st })
        {
            string t = st.FetchedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
            AiStatusText.Text =
                $"  •  AI: min %{st.MinHealth:0.#}  kritik {st.CriticalNodeCount}  sızıntı {st.LeakSegmentCount}  ({t})";
        }
        else if (_source is LiveSnapshotSource live && !string.IsNullOrWhiteSpace(_settings.AiRespondUrl))
        {
            AiStatusText.Text = live.HasAiOverlay ? "" : "  •  AI: bekleniyor…";
        }
        else
        {
            AiStatusText.Text = "";
        }
    }

    // Kritik istasyonlar + AI alarm listesi; rozet + popup (Phase 3).
    private void UpdateAlarms()
    {
        int critStations = 0;
        foreach (var n in PipelineTopology.Nodes)
        {
            if (n.Type != "CS") continue; // petrol kondisyon skoru AI kritik rozetine girmez
            var units = _source.UnitHealths(n.Id);
            bool hasAi = false;
            foreach (var u in units) if (u.HasTelemetry) { hasAi = true; break; }
            if (!hasAi) continue; // AI yok → alarm için uydurma sağlık kullanma
            if (_source.Node(n.Id).Health < _settings.CriticalHealthThreshold) critStations++;
        }

        var open = new List<AiAlarm>();
        if (_source is LiveSnapshotSource live)
        {
            foreach (var a in live.AiAlarms)
            {
                if (_ackedAlarms.Contains(a.Id)) continue;
                open.Add(a);
                if (_loggedAlarms.Add(a.Id))
                    _alerts.SendAlert(a.EntityId, a.Kind, a.Message);
            }
        }

        int aiHealth = open.Count(a => a.Kind == "critical_health");
        int leaks = open.Count(a => a.Kind == "leak");
        // Rozet: harita kritik istasyonları + AI sızıntıları (ünite alarmları popup'ta).
        int badge = critStations + leaks;

        AlarmList.ItemsSource = open;
        if (badge > 0 || open.Count > 0)
        {
            var parts = new List<string>();
            if (critStations > 0) parts.Add($"{critStations} KRİTİK");
            if (aiHealth > 0 && aiHealth != critStations) parts.Add($"{aiHealth} ünite");
            if (leaks > 0) parts.Add($"{leaks} SIZINTI");
            if (parts.Count == 0) parts.Add($"{open.Count} ALARM");
            AlarmText.Text = "⚠  " + string.Join(" · ", parts);
            AlarmButton.Visibility = Visibility.Visible;
        }
        else
        {
            AlarmButton.IsChecked = false;
            AlarmButton.Visibility = Visibility.Collapsed;
        }
    }

    private void AckAlarm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && id.Length > 0)
        {
            _ackedAlarms.Add(id);
            UpdateAlarms();
        }
    }

    private void AckAllAlarms_Click(object sender, RoutedEventArgs e)
    {
        if (_source is LiveSnapshotSource live)
            foreach (var a in live.AiAlarms)
                _ackedAlarms.Add(a.Id);
        UpdateAlarms();
        AlarmButton.IsChecked = false;
    }

    private void OnNodeClicked(PNode n)
    {
        // Tıklanabilir düğümler: gaz kompresör (IsStation, sağlık/sensör paneli),
        // ham petrol pompa/terminal (kimlik + bağlı hatlar) ve depolar. Her iki depo
        // tipi (gaz UGS + petrol deposu) haritada aynı silindir ikonuyla çizilir; bu
        // yüzden ikisi de aynı minimal depo panelini (doluluk + bağlı hatlar) açar.
        bool pump = n.Type is "PS" or "PT";
        bool storage = n.Type is "STORAGE" or "OILDEPO";
        if (!n.IsStation && !pump && !storage)
        {
            SelectedInfo.Text = $"{n.Id}  {n.Name}  —  {n.Type} (izleme dışı)";
            return;
        }

        _selected = n.Id;
        Map.SelectedId = n.Id; // haritada secim halkasi
        _selectedUnit = null;  // istasyon değişince ünite seçimini sıfırla
        DetailId.Text = n.Id;
        DetailName.Text = n.Name;
        HealthPanel.Visibility = n.Type == "CS" ? Visibility.Visible : Visibility.Collapsed;
        PumpPanel.Visibility = pump ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
        LiveTelemetryPanel.Visibility = (n.Type == "CS" || pump) ? Visibility.Visible : Visibility.Collapsed;

        if (n.IsStation || pump)
        {
            _vib.Clear(); _bt.Clear(); _dp.Clear();
            if (pump) ShowPumpDetail(n);
            UpdateDetail();
            UpdateSensorList(n.Id);
            if (n.Type == "CS") ShowAiHealthEvaluation(n.Id);
            if (_source is LiveSnapshotSource live)
                _ = live.FetchNodeDetailTelemetryAsync(n.Id);
        }
        else if (storage)
        {
            ShowStorageDetail(n);
        }

        ShowDetailAnimated();
        SelectedInfo.Text = $"{n.Id}  {n.Name}";
    }

    // Petrol pompa istasyonu / terminali: kimlik + bağlı hatlar (+ LiveTelemetryPanel sensörler).
    private void ShowPumpDetail(PNode n)
    {
        DetailStatus.Text = n.Type == "PT" ? "PETROL TERMİNALİ" : "POMPA İSTASYONU";
        DetailStatusBox.Background = new SolidColorBrush(Color.FromRgb(0x9A, 0xBE, 0x3A));

        DetailPumpType.Text = n.Type == "PT"
            ? "Ham Petrol Pompa Terminali"
            : "Ham Petrol Pompa İstasyonu";
        DetailPumpRegion.Text = System.Globalization.CultureInfo.CurrentCulture.TextInfo
            .ToTitleCase(n.Region.Replace('_', ' '));

        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (var s in PipelineTopology.Segments)
        {
            if (s.From != n.Id && s.To != n.Id) continue;
            string otherId = s.From == n.Id ? s.To : s.From;
            var other = PipelineTopology.NodeById(otherId);
            if (other == null) continue;
            if (count > 0) sb.Append('\n');
            string kapasite = s.Product.StartsWith("gas", StringComparison.OrdinalIgnoreCase)
                ? $"{s.MaxCapacity:0} mcm/gün" : $"{s.MaxCapacity:0}";
            sb.Append($"→ {other.Name}   ({s.Product}, {kapasite})");
            count++;
        }
        DetailPumpConns.Text = count > 0 ? sb.ToString() : "Bağlı hat yok.";
    }

    // Depo minimal detayi (gaz UGS + petrol deposu ortak): doluluk + bagli hatlar.
    // Her iki depo tipi de canli doluluk yuzdesini (s_storage_level_pct) tasir.
    // Panel yapisi her iki depo tipinde ayni kalir; yalnizca kimlik rozeti degisir.
    private void ShowStorageDetail(PNode n)
    {
        bool oil = n.Type == "OILDEPO";
        DetailStatus.Text = oil ? "PETROL DEPO / YÜKLEME" : "DOĞAL GAZ DEPOLAMA (UGS)";
        DetailStatusBox.Background = new SolidColorBrush(oil
            ? Color.FromRgb(0xC8, 0xD1, 0x2E) : Color.FromRgb(0x4E, 0xCD, 0xC4));

        DetailStorageLevel.Text = $"%{_source.Level(n.Id):0}";

        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (var s in PipelineTopology.Segments)
        {
            if (s.From != n.Id && s.To != n.Id) continue;
            string otherId = s.From == n.Id ? s.To : s.From;
            var other = PipelineTopology.NodeById(otherId);
            if (other == null) continue; // uç düğüm topolojide yoksa satırı atla (çökme yerine)
            if (count > 0) sb.Append('\n');
            // Kapasite birimi ürüne göre: gaz hatları mcm/gün; petrol (oil) hattı bu gaz
            // birimini taşımaz — değeri birimsiz göster (ürün adı türü zaten belirtir).
            string kapasite = s.Product.StartsWith("gas", StringComparison.OrdinalIgnoreCase)
                ? $"{s.MaxCapacity:0} mcm/gün" : $"{s.MaxCapacity:0}";
            sb.Append($"→ {other.Name}   ({s.Product}, {kapasite})");
            count++;
        }
        DetailStorageConns.Text = count > 0 ? sb.ToString() : "Bağlı hat yok.";
    }

    // AI respond overlay'den health_state eşdeğeri (sayısal health + durum etiketi).
    private void ShowAiHealthEvaluation(string stationId)
    {
        DetailServerHealth.Visibility = Visibility.Collapsed;
        if (_source is not LiveSnapshotSource { HasAiOverlay: true } live) return;

        var worst = live.WorstAiUnit(stationId);
        if (worst == null)
        {
            DetailServerHealth.Text = "AI: bu istasyon için henüz tahmin yok (sunucu).";
            DetailServerHealth.Visibility = Visibility.Visible;
            return;
        }

        string label = SnapshotSource.AiHealthStateLabel(worst.Value.Pred.Health);
        DetailServerHealth.Text =
            $"AI değerlendirmesi ({label}): %{worst.Value.Pred.Health:0.#} · RUL {worst.Value.Pred.Rul:0.#} · {worst.Value.UnitId}";
        DetailServerHealth.Visibility = Visibility.Visible;
    }

    // Detay panelini sagdan kayarak + belirerek ac (zaten acisa animasyon yok).
    private void ShowDetailAnimated()
    {
        bool wasHidden = Detail.Visibility != Visibility.Visible;
        Detail.Visibility = Visibility.Visible;
        if (!wasHidden) return;
        var tt = (TranslateTransform)Detail.RenderTransform;
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Detail.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        _selectedUnit = null;
        Map.SelectedId = null;
        Detail.Visibility = Visibility.Collapsed;
    }

    private void UpdateDetail()
    {
        if (_selected == null) return;
        bool oilPump = PipelineTopology.NodeById(_selected) is { Type: "PS" or "PT" };

        StationSensors s;
        if (UnitPerfToggle?.IsChecked == true
            && _selectedUnit != null
            && _source is SnapshotSource ss)
        {
            s = ss.SensorsForUnit(_selectedUnit);
            string tag = ShortUnitLabel(_selectedUnit);
            VibLabel.Text = $"Titreşim · {tag}";
            BtLabel.Text = $"Yatak Sıcaklığı · {tag}";
            DpLabel.Text = oilPump ? $"Basma Basıncı · {tag}" : $"Basma Basıncı · {tag}";
        }
        else
        {
            s = _source.Sensors(_selected);
            VibLabel.Text = "Titreşim";
            BtLabel.Text = "Yatak Sıcaklığı";
            DpLabel.Text = "Basma Basıncı";
        }

        Push(_vib, s.Vibration); Push(_bt, s.BearingTemp); Push(_dp, s.DischargePressure);
        VibChart.Values = _vib.ToArray(); BtChart.Values = _bt.ToArray(); DpChart.Values = _dp.ToArray();
        VibVal.Text = $"{s.Vibration:0.00} mm/s";
        BtVal.Text = $"{s.BearingTemp:0.0} °C";
        DpVal.Text = $"{s.DischargePressure:0.00} bar";

        if (oilPump)
            RefreshUnitPerfPanel();
        else
            RenderStationHealth(_source.Node(_selected), _source.UnitHealths(_selected));

        string quality = (_source as SnapshotSource)?.StationDataQuality(_selected) ?? "";
        if (quality.Length > 0 && !quality.Equals("GOOD", StringComparison.OrdinalIgnoreCase))
        {
            DetailQuality.Text = $"⚠ Telemetri kalitesi: {quality}";
            DetailQuality.Visibility = Visibility.Visible;
        }
        else DetailQuality.Visibility = Visibility.Collapsed;
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
        // Petrol pompa (oil_pump) — gazdan farklı anahtarlar
        ["s_suction_pressure_bar"] = ("Emme Basıncı", "bar"),
        ["s_discharge_pressure_bar"] = ("Basma Basıncı", "bar"),
        ["s_flow_m3_h"] = ("Akış", "m³/sa"),
        ["s_pump_power_mw"] = ("Pompa Gücü", "MW"),
        ["s_bearing_temp_c"] = ("Yatak Sıcaklığı", "°C"),
        ["s_vibration_mm_s"] = ("Titreşim", "mm/s"),
        ["s_tank_level_pct"] = ("Tank Doluluk", "%"),
        ["s_inventory_m3"] = ("Envanter", "m³"),
        ["s_throughput_m3_h"] = ("Debi", "m³/sa"),
        ["s_tank_temp_c"] = ("Tank Sıcaklığı", "°C"),
    };

    // Sensor gruplari (detay panelinde alt basliklar; duz liste yerine spec-sheet).
    private static readonly (string Baslik, string[] Anahtarlar)[] SensorGruplari =
    {
        ("BASINÇ", new[] { "s_1_suction_pressure_bar", "s_2_discharge_pressure_bar",
                           "s_3_pressure_ratio", "s_12_lube_oil_pressure_bar",
                           "s_15_seal_gas_pressure_bar", "s_20_filter_dp_bar",
                           "s_suction_pressure_bar", "s_discharge_pressure_bar" }),
        ("SICAKLIK", new[] { "s_4_suction_temp_c", "s_5_discharge_temp_c",
                             "s_10_bearing_temp_1_c", "s_11_bearing_temp_2_c",
                             "s_13_lube_oil_temp_c", "s_bearing_temp_c", "s_tank_temp_c" }),
        ("TİTREŞİM & MEKANİK", new[] { "s_6_shaft_rpm", "s_7_vibration_de_mm_s",
                                       "s_8_vibration_nde_mm_s", "s_9_axial_displacement_mm",
                                       "s_21_torque_nm", "s_vibration_mm_s" }),
        ("AKIŞ & PERFORMANS", new[] { "s_14_gas_flow_meter_m3_h", "s_16_gas_flow_m3_h",
                                      "s_17_power_mw", "s_18_polytropic_efficiency",
                                      "s_19_surge_margin_pct", "s_flow_m3_h", "s_pump_power_mw",
                                      "s_tank_level_pct", "s_inventory_m3", "s_throughput_m3_h" }),
    };

    // Nominal bant asimi esikleri: anahtar -> (uyari, kritik). Asan deger renklenir.
    private static readonly Dictionary<string, (double Uyari, double Kritik)> SensorEsikleri = new()
    {
        ["s_7_vibration_de_mm_s"] = (4.5, 7.1),   // ISO 10816 bolge sinirlarina yakin
        ["s_8_vibration_nde_mm_s"] = (4.5, 7.1),
        ["s_10_bearing_temp_1_c"] = (80, 95),
        ["s_11_bearing_temp_2_c"] = (80, 95),
        ["s_13_lube_oil_temp_c"] = (60, 75),
        ["s_vibration_mm_s"] = (4.0, 5.5),
        ["s_bearing_temp_c"] = (75, 85),
        ["s_discharge_pressure_bar"] = (95, 100),
    };

    private static int AlertSeviyesi(string key, double v) =>
        SensorEsikleri.TryGetValue(key, out var e) ? (v > e.Kritik ? 2 : v > e.Uyari ? 1 : 0) : 0;

    // Detay panelinin sensor listesini doldur (secili unite veya en kotu).
    private void UpdateSensorList(string stationId)
    {
        if (_source is not SnapshotSource snap)
        {
            SensorHeader.Visibility = Visibility.Collapsed;
            SensorList.Visibility = Visibility.Collapsed;
            return;
        }

        string? unitId = null;
        IReadOnlyDictionary<string, double>? sensors = null;

        if (UnitPerfToggle?.IsChecked == true && _selectedUnit != null)
        {
            unitId = _selectedUnit;
            sensors = snap.UnitTelemetry(_selectedUnit);
        }

        if (sensors == null || sensors.Count == 0)
        {
            if (snap.WorstUnitSensors(stationId) is { } wu)
            {
                unitId = wu.UnitId;
                sensors = wu.Sensors;
            }
        }

        if (unitId != null && sensors != null && sensors.Count > 0)
        {
            SensorHeader.Text = $"TÜM SENSÖRLER — {unitId}";

            var rows = new List<SensorRow>();
            var kalan = sensors.Where(kv => kv.Key.StartsWith("s_"))
                                  .ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (baslik, anahtarlar) in SensorGruplari)
            {
                var grup = anahtarlar.Where(kalan.ContainsKey).ToList();
                if (grup.Count == 0) continue;
                rows.Add(new SensorRow(baslik, "", IsHeader: true));
                foreach (var key in grup)
                {
                    rows.Add(SatirYap(key, kalan[key]));
                    kalan.Remove(key);
                }
            }
            if (kalan.Count > 0) // gruplara girmeyen (bilinmeyen) sensorler
            {
                rows.Add(new SensorRow("DİĞER", "", IsHeader: true));
                foreach (var kv in kalan.OrderBy(kv => SensorSira(kv.Key)))
                    rows.Add(SatirYap(kv.Key, kv.Value));
            }

            SensorList.ItemsSource = rows;
            SensorHeader.Visibility = Visibility.Visible;
            SensorList.Visibility = Visibility.Visible;
        }
        else
        {
            SensorHeader.Visibility = Visibility.Collapsed;
            SensorList.Visibility = Visibility.Collapsed;
        }
    }

    private static SensorRow SatirYap(string key, double deger)
    {
        var (ad, birim) = SensorAdlari.TryGetValue(key, out var s)
            ? s : (GenelSensorAdi(key), "");
        string metin = deger.ToString("0.##");
        return new SensorRow(ad, birim.Length > 0 ? $"{metin} {birim}" : metin,
            Alert: AlertSeviyesi(key, deger));
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

    // Tema degisti: harita paleti guncellendi, yeniden ciz.
    private void OnThemeChanged()
    {
        Map.InvalidateVisual();
        RefreshPieSepSwatch();
    }

    protected override void OnClosed(EventArgs e)
    {
        _render.Stop();
        ThemeManager.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }
}
