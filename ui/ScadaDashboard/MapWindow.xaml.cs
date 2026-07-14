using System.Windows;
using System.Windows.Threading;
using ScadaDashboard.Pipeline;

namespace ScadaDashboard;

public partial class MapWindow : Window
{
    private readonly PipelineSimulator _source = new();
    private readonly DispatcherTimer _render;
    private int _renderTicks;

    public MapWindow()
    {
        InitializeComponent();

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
            }
            if (AlarmBox.Visibility == Visibility.Visible)   // yanip sonme
                AlarmBox.Opacity = 0.4 + 0.6 * (0.5 + 0.5 * Math.Sin(Environment.TickCount / 250.0));
            Map.InvalidateVisual();
        };
        UpdateAlarms();
        _render.Start();
    }

    // Kritik istasyonlari say, ust bar alarm rozetini guncelle.
    private void UpdateAlarms()
    {
        int crit = 0;
        foreach (var n in PipelineTopology.Nodes)
            if (n.IsStation && _source.Node(n.Id).Health < 20) crit++;
        if (crit > 0)
        {
            AlarmText.Text = $"⚠  {crit} KRITIK ISTASYON";
            AlarmBox.Visibility = Visibility.Visible;
        }
        else AlarmBox.Visibility = Visibility.Collapsed;
    }

    private void OnNodeClicked(PNode n)
    {
        if (n.IsStation)
        {
            var s = _source.Node(n.Id);
            string durum = s.Health >= 70 ? "SAGLIKLI" : s.Health >= 40 ? "UYARI"
                         : s.Health >= 20 ? "RISKLI" : "KRITIK";
            SelectedInfo.Text = $"{n.Id}  {n.Name}  —  {durum}   Saglik %{s.Health:0}   Kalan omur {s.Rul:0} dongu";
        }
        else
        {
            SelectedInfo.Text = $"{n.Id}  {n.Name}  —  {n.Type} (izleme disi)";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _render.Stop();
        base.OnClosed(e);
    }
}
