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

    /// <summary>
    /// Gizlenen dugum kategorileri: "CS", "BORDER", "OFFTAKE", "STORAGE",
    /// "OTHER" (kavsak/LNG/FSRU/terminal). Alt bar onay kutulari doldurur.
    /// Segmentler cizilmeye devam eder; yalnizca daire + etiket gizlenir.
    /// </summary>
    public HashSet<string> HiddenCategories { get; } = new();

    private bool IsHidden(PNode n) => HiddenCategories.Contains(CategoryOf(n));

    private static string CategoryOf(PNode n) =>
        n.IsStation || n.Type == "CS" ? "CS"
        : n.Type is "BORDER" or "OFFTAKE" or "STORAGE" ? n.Type
        : "OTHER";

    private readonly Dictionary<string, Point> _screen = new();
    // Segment ekran noktalari (polyline guzergah; hover mesafesi icin cache).
    private readonly Dictionary<string, Point[]> _segScreen = new();

    private string? _hoverId;   // uzerine gelinen dugum/segment
    private bool _hoverSeg;
    private Point _mouse;

    // Provins geometrisi cache (boyut/zoom/pan degisince yeniden kurulur).
    private Geometry? _provGeo;
    private double _pkW, _pkH, _pkZoom = 1, _pkPanX, _pkPanY;

    // Zoom + pan durumu.
    private double _zoom = 1, _panX, _panY;
    private bool _dragging, _moved;
    private Point _dragStart;
    private double _panStartX, _panStartY;

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
        "STORAGE" => Color.FromRgb(0x4E, 0xCD, 0xC4),
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

        Func<double, double, Point> geoProj;
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
            geoProj = (lat, lon) => new Point(ox + (lon - _minLon) * k * scale, oy + (_maxLat - lat) * scale);
        }
        else
        {
            double minLat = nodes.Min(n => n.Lat), maxLat = nodes.Max(n => n.Lat);
            double minLon = nodes.Min(n => n.Lon), maxLon = nodes.Max(n => n.Lon);
            const double pad = 70;
            geoProj = (lat, lon) => new Point(
                pad + (lon - minLon) / Math.Max(1e-6, maxLon - minLon) * (w - 2 * pad),
                pad + (maxLat - lat) / Math.Max(1e-6, maxLat - minLat) * (h - 2 * pad));
        }

        // Zoom + pan (etiketler sabit boyutta kalır, konumlar dönüşür).
        Func<double, double, Point> project = (lat, lon) =>
        {
            var b = geoProj(lat, lon);
            return new Point(b.X * _zoom + _panX, b.Y * _zoom + _panY);
        };

        if (hasMap)
        {
            if (_provGeo == null || _pkW != w || _pkH != h
                || _pkZoom != _zoom || _pkPanX != _panX || _pkPanY != _panY)
            {
                _provGeo = BuildProvinceGeometry(project);
                _pkW = w; _pkH = h; _pkZoom = _zoom; _pkPanX = _panX; _pkPanY = _panY;
            }
            dc.DrawGeometry(new SolidColorBrush(Land),
                            new Pen(new SolidColorBrush(Province), 0.7), _provGeo);
        }

        _screen.Clear();
        foreach (var n in nodes)
            _screen[n.Id] = project(n.Lat, n.Lon);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double phase = (Environment.TickCount % 2000) / 2000.0;
        var lowCol = Color.FromRgb(0x4E, 0xCD, 0xC4);
        var highCol = Color.FromRgb(0xFF, 0x6B, 0x6B);

        // --- Segmentler ---
        _segScreen.Clear();
        foreach (var seg in PipelineTopology.Segments)
        {
            // Ucu bilinmeyen segmenti atla (bozuk snapshot verisine karsi).
            if (!_screen.TryGetValue(seg.From, out var pFrom)
                || !_screen.TryGetValue(seg.To, out var pTo)) continue;

            // Guzergah: Geometry doluysa gercek polyline, yoksa duz cizgi.
            Point[] pts;
            if (seg.Geometry is { Count: >= 2 } geo)
            {
                pts = new Point[geo.Count];
                for (int i = 0; i < geo.Count; i++)
                    pts[i] = project(geo[i].Lat, geo[i].Lon);
            }
            else pts = new[] { pFrom, pTo };
            _segScreen[seg.Id] = pts;

            var snap = _source.Segment(seg.Id);
            var col = Lerp(lowCol, highCol, snap.LoadRatio);
            double thick = 2.5 + snap.LoadRatio * 4.0;

            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)), thick + 3);
            var mainPen = new Pen(new SolidColorBrush(col), thick);
            for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(glowPen, pts[i], pts[i + 1]);
            for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(mainPen, pts[i], pts[i + 1]);

            // Sizinti: yanip sonen kirmizi kalin katman.
            if (snap.Leak)
            {
                double lp = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 200.0);
                byte la = (byte)(60 + lp * 190);
                var leakPen = new Pen(new SolidColorBrush(Color.FromArgb(la, 0xE7, 0x4C, 0x3C)), thick + 7);
                for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(leakPen, pts[i], pts[i + 1]);
            }

            // Toplam guzergah uzunlugu (akis noktalari + etiket konumu icin).
            double segLen = 0;
            for (int i = 0; i + 1 < pts.Length; i++) segLen += (pts[i + 1] - pts[i]).Length;

            var dotBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF6));
            const int dots = 4;
            for (int i = 0; i < dots; i++)
            {
                double t = (phase + (double)i / dots) % 1.0;
                dc.DrawEllipse(dotBrush, null, PointAlong(pts, segLen * t), 2.0, 2.0);
            }

            // Etiket: guzergahin orta noktasi, yerel yone dik yukari kaydirilir.
            var mid = PointAlong(pts, segLen / 2, out var dir);
            var perp = new Vector(-dir.Y, dir.X); if (perp.Y > 0) perp.Negate(); // etiket yukari tarafa
            var lc = new Point(mid.X + perp.X * 16, mid.Y + perp.Y * 16);
            if (segLen > 150) // kisa segmentte etiket dugum yazilariyla cakisir, atla
            {
                var ft = Text($"{snap.FlowMcmDay:0} mcm/g", 10.5, MutedCol, dpi);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, Bg.R, Bg.G, Bg.B)), null,
                    new Rect(lc.X - ft.Width / 2 - 3, lc.Y - ft.Height / 2 - 1, ft.Width + 6, ft.Height + 2));
                dc.DrawText(ft, new Point(lc.X - ft.Width / 2, lc.Y - ft.Height / 2));
            }

            if (snap.Leak)
            {
                var lc2 = new Point(mid.X - perp.X * 16, mid.Y - perp.Y * 16); // diger tarafa
                var lft = Text("⚠ SIZINTI", 11, Color.FromRgb(0xE7, 0x4C, 0x3C), dpi);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, Bg.R, Bg.G, Bg.B)), null,
                    new Rect(lc2.X - lft.Width / 2 - 3, lc2.Y - lft.Height / 2 - 1, lft.Width + 6, lft.Height + 2));
                dc.DrawText(lft, new Point(lc2.X - lft.Width / 2, lc2.Y - lft.Height / 2));
            }
        }

        // --- Düğümler ---
        // Yogun agda (snapshot: ~90 dugum) her dugume etiket sigmaz; yalnizca
        // istasyon etiketleri cizilir, digerleri tooltip ile okunur.
        bool dense = nodes.Count > 30;
        foreach (var n in nodes)
        {
            if (IsHidden(n)) continue; // kategori gizli: daire + etiket cizilmez
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

            // Depo: yandan tank doluluk gostergesi.
            if (n.Type == "STORAGE")
            {
                double lvl = _source.Level(n.Id);
                var tank = new Rect(p.X + r + 6, p.Y - 12, 11, 24);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, Bg.R, Bg.G, Bg.B)),
                    new Pen(new SolidColorBrush(MutedCol), 1), tank);
                double fh = tank.Height * Math.Clamp(lvl, 0, 100) / 100.0;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4E, 0xCD, 0xC4)), null,
                    new Rect(tank.X, tank.Bottom - fh, tank.Width, fh));
            }

            if (!dense || n.IsStation)
            {
                var name = Text(n.Name, 11.5, TextCol, dpi);
                DrawLabel(dc, name, new Point(p.X - name.Width / 2, p.Y + r + 4));
                string sub = n.IsStation ? $"%{snap.Health:0}  •  RUL {snap.Rul:0}"
                           : n.Type == "STORAGE" ? $"DEPO  %{_source.Level(n.Id):0}"
                           : n.Type;
                var subFt = Text(sub, 10, MutedCol, dpi);
                DrawLabel(dc, subFt, new Point(p.X - subFt.Width / 2, p.Y + r + 20));
            }
        }

        DrawTooltip(dc, dpi);
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
        _dragging = true; _moved = false;
        _dragStart = e.GetPosition(this);
        _panStartX = _panX; _panStartY = _panY;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false; ReleaseMouseCapture();
        if (_moved) return; // sürüklemeyse tıklama sayma
        var click = e.GetPosition(this);
        foreach (var n in PipelineTopology.Nodes)
            if (!IsHidden(n) && _screen.TryGetValue(n.Id, out var p) && (click - p).Length <= 26)
            { NodeClicked?.Invoke(n); break; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_dragging)
        {
            var d = _mouse - _dragStart;
            if (d.Length > 3) _moved = true;
            _panX = _panStartX + d.X; _panY = _panStartY + d.Y;
            _hoverId = null;
            InvalidateVisual();
            return;
        }
        _hoverId = null;
        foreach (var n in PipelineTopology.Nodes)
            if (!IsHidden(n) && _screen.TryGetValue(n.Id, out var p) && (_mouse - p).Length <= 18)
            { _hoverId = n.Id; _hoverSeg = false; return; }
        foreach (var s in PipelineTopology.Segments)
            if (_segScreen.TryGetValue(s.Id, out var pts) && DistToPolyline(_mouse, pts) <= 7)
            { _hoverId = s.Id; _hoverSeg = true; return; }
    }

    protected override void OnMouseLeave(MouseEventArgs e) => _hoverId = null;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double nz = Math.Clamp(_zoom * f, 1.0, 6.0);
        f = nz / _zoom;
        var m = e.GetPosition(this);
        _panX = m.X - (m.X - _panX) * f;   // imlec altindaki nokta sabit kalir
        _panY = m.Y - (m.Y - _panY) * f;
        _zoom = nz;
        if (_zoom <= 1.0001) { _zoom = 1; _panX = 0; _panY = 0; } // tam sigdir
        InvalidateVisual();
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        _zoom = 1; _panX = 0; _panY = 0; // sifirla
        InvalidateVisual();
    }

    /// <summary>Polyline uzerinde bastan itibaren verilen mesafedeki nokta.</summary>
    private static Point PointAlong(Point[] pts, double dist) => PointAlong(pts, dist, out _);

    private static Point PointAlong(Point[] pts, double dist, out Vector dir)
    {
        dir = new Vector(1, 0);
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var d = pts[i + 1] - pts[i];
            double len = d.Length;
            if (len < 1e-9) continue;
            if (dist <= len || i + 2 == pts.Length)
            {
                dir = d / len;
                double t = Math.Clamp(dist / len, 0, 1);
                return pts[i] + d * t;
            }
            dist -= len;
        }
        return pts[^1];
    }

    /// <summary>Noktanin polyline'a en kisa mesafesi (hover icin).</summary>
    private static double DistToPolyline(Point p, Point[] pts)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Length; i++)
            best = Math.Min(best, DistToSeg(p, pts[i], pts[i + 1]));
        return best;
    }

    private static double DistToSeg(Point p, Point a, Point b)
    {
        var ab = b - a; double len2 = ab.LengthSquared;
        if (len2 < 1e-6) return (p - a).Length;
        double t = Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private void DrawTooltip(DrawingContext dc, double dpi)
    {
        if (_hoverId == null || _source == null) return;
        string text;
        if (_hoverSeg)
        {
            var s = PipelineTopology.Segments.First(x => x.Id == _hoverId);
            var snap = _source.Segment(s.Id);
            text = $"{s.Id}   {s.From} → {s.To}\nAkış: {snap.FlowMcmDay:0} mcm/gün"
                 + (snap.Leak ? "\n⚠ SIZINTI" : "");
        }
        else
        {
            var n = PipelineTopology.NodeById(_hoverId);
            if (n.IsStation)
            {
                var snap = _source.Node(n.Id);
                string durum = snap.Health >= 70 ? "SAĞLIKLI" : snap.Health >= 40 ? "UYARI"
                             : snap.Health >= 20 ? "RİSKLİ" : "KRİTİK";
                text = $"{n.Id}  {n.Name}\n{durum}   %{snap.Health:0}   RUL {snap.Rul:0}";
            }
            else if (n.Type == "STORAGE")
                text = $"{n.Id}  {n.Name}\nDepo doluluk: %{_source.Level(n.Id):0}";
            else
                text = $"{n.Id}  {n.Name}\n{n.Type}";
        }

        var ft = Text(text, 11.5, TextCol, dpi);
        double pad = 8, w = ft.Width + 2 * pad, h = ft.Height + 2 * pad;
        double x = Math.Min(_mouse.X + 14, ActualWidth - w - 4);
        double y = Math.Min(_mouse.Y + 14, ActualHeight - h - 4);
        var rect = new Rect(x, y, w, h);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x26, 0x34)),
            new Pen(new SolidColorBrush(Province), 1), rect);
        dc.DrawText(ft, new Point(x + pad, y + pad));
    }
}
