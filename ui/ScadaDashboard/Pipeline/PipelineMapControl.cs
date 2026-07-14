using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Boru hattı ağını çizen canlı harita kontrolü. Arka planda kendi vektör
/// Türkiye haritamızı (gömülü il sınırları) çizer; üstüne segmentleri doluluğa
/// göre renklendirip akış yönünde kayan noktalarla canlandırır ve düğümleri
/// sağlık durumuna göre renklendirir. Harita ve düğümler aynı coğrafi
/// projeksiyonu kullandığından hizalama otomatiktir. Harici NuGet gerekmez.
/// </summary>
public sealed class PipelineMapControl : Control
{
    private static readonly Color Bg = Color.FromRgb(0x0F, 0x16, 0x20);       // deniz / zemin
    private static readonly Color Land = Color.FromRgb(0x18, 0x24, 0x32);     // kara dolgusu
    private static readonly Color Province = Color.FromRgb(0x33, 0x47, 0x5C); // il sınırı
    private static readonly Color TextCol = Color.FromRgb(0xE6, 0xEE, 0xF6);
    private static readonly Color MutedCol = Color.FromRgb(0x8C, 0xA3, 0xB8);

    private IPipelineSource? _source;
    public IPipelineSource? Source
    {
        get => _source;
        set { _source = value; InvalidateVisual(); }
    }

    public event Action<PNode>? NodeClicked;

    private readonly Dictionary<string, Point> _screen = new();

    // Provins geometrisi ekran boyutuna gore cache'lenir (her karede yeniden kurma).
    private Geometry? _provGeo;
    private double _provW, _provH;

    // Il sinirlarinin cografi kutusu (bir kez hesaplanir).
    private static bool _bboxReady;
    private static double _minLon, _maxLon, _minLat, _maxLat;

    static PipelineMapControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(PipelineMapControl), new FrameworkPropertyMetadata(typeof(PipelineMapControl)));
    }

    private static void EnsureBbox()
    {
        if (_bboxReady) return;
        _bboxReady = true;
        _minLon = _minLat = 1e9; _maxLon = _maxLat = -1e9;
        foreach (var p in TurkeyMap.Provinces)
            foreach (var ring in p.Rings)
                foreach (var pt in ring)
                {
                    if (pt[0] < _minLon) _minLon = pt[0]; if (pt[0] > _maxLon) _maxLon = pt[0];
                    if (pt[1] < _minLat) _minLat = pt[1]; if (pt[1] > _maxLat) _maxLat = pt[1];
                }
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

        EnsureBbox();
        var nodes = PipelineTopology.Nodes;
        bool hasMap = TurkeyMap.Provinces.Count > 0 && _maxLon > _minLon;

        Func<double, double, Point> project;
        if (hasMap)
        {
            // En-boy düzeltmeli projeksiyon: boylam dereceleri enlem cos'u kadar kısalır.
            double midLat = (_minLat + _maxLat) / 2.0;
            double k = Math.Cos(midLat * Math.PI / 180.0);
            double scaledW = (_maxLon - _minLon) * k;
            double scaledH = (_maxLat - _minLat);
            const double pad = 40;
            double scale = Math.Min((w - 2 * pad) / scaledW, (h - 2 * pad) / scaledH);
            double ox = (w - scaledW * scale) / 2.0;
            double oy = (h - scaledH * scale) / 2.0;
            project = (lat, lon) => new Point(ox + (lon - _minLon) * k * scale, oy + (_maxLat - lat) * scale);

            // Provins geometrisi (boyut degistiyse yeniden kur).
            if (_provGeo == null || _provW != w || _provH != h)
            {
                _provGeo = BuildProvinceGeometry(project);
                _provW = w; _provH = h;
            }
            dc.DrawGeometry(new SolidColorBrush(Land),
                            new Pen(new SolidColorBrush(Province), 0.7), _provGeo);
        }
        else
        {
            double minLat = nodes.Min(n => n.Lat), maxLat = nodes.Max(n => n.Lat);
            double minLon = nodes.Min(n => n.Lon), maxLon = nodes.Max(n => n.Lon);
            const double pad = 70;
            project = (lat, lon) => new Point(
                pad + (lon - minLon) / Math.Max(1e-6, maxLon - minLon) * (w - 2 * pad),
                pad + (maxLat - lat) / Math.Max(1e-6, maxLat - minLat) * (h - 2 * pad));
        }

        _screen.Clear();
        foreach (var n in nodes)
            _screen[n.Id] = project(n.Lat, n.Lon);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double phase = (Environment.TickCount % 2000) / 2000.0;
        var lowCol = Color.FromRgb(0x4E, 0xCD, 0xC4);
        var highCol = Color.FromRgb(0xFF, 0x6B, 0x6B);

        // --- Segmentler ---
        foreach (var seg in PipelineTopology.Segments)
        {
            var p1 = _screen[seg.From];
            var p2 = _screen[seg.To];
            var snap = _source.Segment(seg.Id);
            var col = Lerp(lowCol, highCol, snap.LoadRatio);
            double thick = 2.5 + snap.LoadRatio * 4.0;

            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)), thick + 3), p1, p2);
            dc.DrawLine(new Pen(new SolidColorBrush(col), thick), p1, p2);

            var dotBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF6));
            const int dots = 4;
            for (int i = 0; i < dots; i++)
            {
                double t = (phase + (double)i / dots) % 1.0;
                var p = new Point(p1.X + (p2.X - p1.X) * t, p1.Y + (p2.Y - p1.Y) * t);
                dc.DrawEllipse(dotBrush, null, p, 2.0, 2.0);
            }

            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var ft = Text($"{snap.FlowMcmDay:0} mcm/g", 10.5, MutedCol, dpi);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, Bg.R, Bg.G, Bg.B)), null,
                new Rect(mid.X - ft.Width / 2 - 3, mid.Y - 17, ft.Width + 6, ft.Height + 2));
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
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, halo.R, halo.G, halo.B)), null, p, r + 6, r + 6);

            // Kritik istasyon: kirmizi yanip sonen halka (alarm).
            if (n.IsStation && snap.Health < 20)
            {
                double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 300.0);
                byte a = (byte)(40 + pulse * 190);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(a, 0xE7, 0x4C, 0x3C)), 3), p, r + 10, r + 10);
            }

            dc.DrawEllipse(fill, new Pen(new SolidColorBrush(Bg), 2), p, r, r);

            var name = Text(n.Name, 11.5, TextCol, dpi);
            DrawLabel(dc, name, new Point(p.X - name.Width / 2, p.Y + r + 4));
            string sub = n.IsStation ? $"%{snap.Health:0}  •  RUL {snap.Rul:0}" : n.Type;
            var subFt = Text(sub, 10, MutedCol, dpi);
            DrawLabel(dc, subFt, new Point(p.X - subFt.Width / 2, p.Y + r + 20));
        }
    }

    private Geometry BuildProvinceGeometry(Func<double, double, Point> project)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var prov in TurkeyMap.Provinces)
                foreach (var ring in prov.Rings)
                {
                    if (ring.Length < 3) continue;
                    ctx.BeginFigure(project(ring[0][1], ring[0][0]), true, true);
                    var pts = new List<Point>(ring.Length - 1);
                    for (int i = 1; i < ring.Length; i++)
                        pts.Add(project(ring[i][1], ring[i][0]));
                    ctx.PolyLineTo(pts, true, false);
                }
        }
        geo.Freeze();
        return geo;
    }

    private static void DrawLabel(DrawingContext dc, FormattedText ft, Point at)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, Bg.R, Bg.G, Bg.B)), null,
            new Rect(at.X - 3, at.Y, ft.Width + 6, ft.Height + 1));
        dc.DrawText(ft, at);
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
