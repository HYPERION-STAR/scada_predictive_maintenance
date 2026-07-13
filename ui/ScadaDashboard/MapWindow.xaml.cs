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
            }
            Map.InvalidateVisual();
        };
        _render.Start();
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
