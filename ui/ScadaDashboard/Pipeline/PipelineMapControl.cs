using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Boru hattı ağını çizen canlı harita kontrolü. Segmentleri doluluğa göre
/// renklendirir + akış yönünde kayan noktalarla canlandırır; düğümleri sağlık
/// durumuna göre renklendirir. Harici NuGet gerektirmez (kendi OnRender'ı).
/// </summary>
public sealed class PipelineMapControl : Control
{
    private static readonly Color Bg = Color.FromRgb(0x0F, 0x16, 0x20);
    private static readonly Color Stroke = Color.FromRgb(0x2E, 0x42, 0x58);
    private static readonly Color TextCol = Color.FromRgb(0xE6, 0xEE, 0xF6);
    private static readonly Color MutedCol = Color.FromRgb(0x8C, 0xA3, 0xB8);

    private IPipelineSource? _source;
    public IPipelineSource? Source
    {
        get => _source;
        set { _source = value; InvalidateVisual(); }
    }

    /// <summary>Kullanıcı bir istasyona tıklayınca (id) tetiklenir.</summary>
    public event Action<PNode>? NodeClicked;

    private readonly Dictionary<string, Point> _screen = new();

    static PipelineMapControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(PipelineMapControl), new FrameworkPropertyMetadata(typeof(PipelineMapControl)));
    }

    private Point Project(double lat, double lon, double w, double h, double pad,
                          double minLat, double maxLat, double minLon, double maxLon)
    {
        double x = pad + (lon - minLon) / Math.Max(1e-6, maxLon - minLon) * (w - 2 * pad);
        double y = pad + (maxLat - lat) / Math.Max(1e-6, maxLat - minLat) * (h - 2 * pad);
        return new Point(x, y);
    }

    private static Brush HealthBrush(double h)
    {
        Color c = h >= 70 ? Color.FromRgb(0x2E, 0xCC, 0x71)
                : h >= 40 ? Color.FromRgb(0xF1, 0xC4, 0x0F)
                : h >= 20 ? Color.FromRgb(0xE6, 0x7E, 0x22)
                : Color.FromRgb(0xE7, 0x4C, 0x3C);
        return new SolidColorBrush(c);
    }

    private static Color NodeTypeColor(string type) => type switch
    {
        "BORDER" => Color.FromRgb(0x4E, 0x9B, 0xF5),
        "OFFTAKE" => Color.FromRgb(0xC5, 0x8A, 0xF5),
        _ => Color.FromRgb(0x8C, 0xA3, 0xB8),
    };

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(0, 0, w, h));
        if (_source == null || w < 40 || h < 40) return;

        var nodes = PipelineTopology.Nodes;
        double minLat = nodes.Min(n => n.Lat), maxLat = nodes.Max(n => n.Lat);
        double minLon = nodes.Min(n => n.Lon), maxLon = nodes.Max(n => n.Lon);
        const double pad = 70;

        _screen.Clear();
        foreach (var n in nodes)
            _screen[n.Id] = Project(n.Lat, n.Lon, w, h, pad, minLat, maxLat, minLon, maxLon);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double phase = (Environment.TickCount % 2000) / 2000.0; // akış animasyonu fazı

        var lowCol = Color.FromRgb(0x4E, 0xCD, 0xC4);  // düşük akış (teal)
        var highCol = Color.FromRgb(0xFF, 0x6B, 0x6B); // yüksek akış (kırmızımsı)

        // --- Segmentler ---
        foreach (var seg in PipelineTopology.Segments)
        {
            var p1 = _screen[seg.From];
            var p2 = _screen[seg.To];
            var snap = _source.Segment(seg.Id);
            var col = Lerp(lowCol, highCol, snap.LoadRatio);
            double thick = 2.5 + snap.LoadRatio * 4.0;

            var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)), thick + 3);
            dc.DrawLine(pen, p1, p2);
            dc.DrawLine(new Pen(new SolidColorBrush(col), thick), p1, p2);

            // Akış yönünde kayan noktalar
            var dotBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF6));
            const int dots = 4;
            for (int i = 0; i < dots; i++)
            {
                double t = (phase + (double)i / dots) % 1.0;
                var p = new Point(p1.X + (p2.X - p1.X) * t, p1.Y + (p2.Y - p1.Y) * t);
                dc.DrawEllipse(dotBrush, null, p, 2.0, 2.0);
            }

            // Akış etiketi (segment ortası)
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var ft = Text($"{snap.FlowMcmDay:0} mcm/g", 10.5, MutedCol, dpi);
            dc.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - 16));
        }

        // --- Düğümler ---
        foreach (var n in nodes)
        {
            var p = _screen[n.Id];
            var snap = _source.Node(n.Id);
            double r = n.IsStation ? 12 : 8;

            Brush fill = n.IsStation ? HealthBrush(snap.Health)
                                     : new SolidColorBrush(NodeTypeColor(n.Type));
            var halo = ((SolidColorBrush)fill).Color;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, halo.R, halo.G, halo.B)), null, p, r + 6, r + 6);
            dc.DrawEllipse(fill, new Pen(new SolidColorBrush(Bg), 2), p, r, r);

            // İsim etiketi
            var name = Text(n.Name, 11.5, TextCol, dpi);
            dc.DrawText(name, new Point(p.X - name.Width / 2, p.Y + r + 4));
            // Tip / sağlık alt etiket
            string sub = n.IsStation ? $"%{snap.Health:0}  •  RUL {snap.Rul:0}" : n.Type;
            var subFt = Text(sub, 10, MutedCol, dpi);
            dc.DrawText(subFt, new Point(p.X - subFt.Width / 2, p.Y + r + 20));
        }
    }

    private static FormattedText Text(string s, double size, Color c, double dpi) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, new SolidColorBrush(c), dpi);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var click = e.GetPosition(this);
        foreach (var n in PipelineTopology.Nodes)
        {
            if (_screen.TryGetValue(n.Id, out var p) && (click - p).Length <= 16)
            {
                NodeClicked?.Invoke(n);
                break;
            }
        }
    }
}
