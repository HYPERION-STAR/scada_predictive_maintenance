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
    // Tema-bagimli renkler (ThemeManager.ApplyTheme ile koyu/acik arasinda gecer).
    private static Color Bg = C(0x0F, 0x16, 0x20);        // notr zemin (dugum kenari, etiket arkasi)
    private static Color Sea = C(0x0D, 0x24, 0x3A);       // deniz (belirgin mavi)
    private static Color Land = C(0x1B, 0x27, 0x33);      // kara dolgusu (Turkiye)
    private static Color NeighLand = C(0x14, 0x1D, 0x29); // komsu ulke
    private static Color NeighEdge = C(0x3C, 0x4E, 0x60); // kiyi/sinir cizgisi
    private static Color Province = C(0x36, 0x45, 0x55);  // il sınırı
    private static Color TextCol = C(0xE6, 0xEE, 0xF6);
    private static Color MutedCol = C(0x9A, 0xAF, 0xC4);
    private static Color FlowDash = C(0xDF, 0xEA, 0xF3);  // borudaki akis cizgileri
    private static Color PipeSheen = C(0xFF, 0xFF, 0xFF); // boru parlak sheen
    private static Color TipBg = C(0x1B, 0x28, 0x38);     // ipucu kutusu zemin
    private static Color TipBorder = C(0x36, 0x45, 0x55); // ipucu kenar
    private static byte SheenA = 70;                      // sheen opakligi (temaya gore)
    private static Color GeoSeaCol = C(0x5B, 0x76, 0x8E); // deniz etiketi
    private static Color GeoCountryCol = C(0x6C, 0x7C, 0x8C); // ulke etiketi

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Harita paletini koyu/açık tema arasında değiştirir (ThemeManager çağırır).</summary>
    public static void ApplyTheme(bool light)
    {
        if (light)
        {
            Bg = C(0xEE, 0xF2, 0xF6); Sea = C(0xB6, 0xD2, 0xE8); Land = C(0xE8, 0xEE, 0xF4);
            NeighLand = C(0xDB, 0xE3, 0xEC); NeighEdge = C(0x9D, 0xB0, 0xC2); Province = C(0xC2, 0xCF, 0xDC);
            TextCol = C(0x16, 0x20, 0x2B); MutedCol = C(0x5B, 0x6B, 0x7C);
            FlowDash = C(0x2A, 0x3C, 0x4E); PipeSheen = C(0xFF, 0xFF, 0xFF); SheenA = 40;
            TipBg = C(0xFF, 0xFF, 0xFF); TipBorder = C(0xC2, 0xCF, 0xDC);
            GeoSeaCol = C(0x5E, 0x7B, 0x97); GeoCountryCol = C(0x7B, 0x8B, 0x9C);
        }
        else
        {
            Bg = C(0x0F, 0x16, 0x20); Sea = C(0x0D, 0x24, 0x3A); Land = C(0x1B, 0x27, 0x33);
            NeighLand = C(0x14, 0x1D, 0x29); NeighEdge = C(0x3C, 0x4E, 0x60); Province = C(0x36, 0x45, 0x55);
            TextCol = C(0xE6, 0xEE, 0xF6); MutedCol = C(0x9A, 0xAF, 0xC4);
            FlowDash = C(0xDF, 0xEA, 0xF3); PipeSheen = C(0xFF, 0xFF, 0xFF); SheenA = 70;
            TipBg = C(0x1B, 0x28, 0x38); TipBorder = C(0x36, 0x45, 0x55);
            GeoSeaCol = C(0x5B, 0x76, 0x8E); GeoCountryCol = C(0x6C, 0x7C, 0x8C);
        }
    }

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

    // --- Katman gorunurlugu (ozellestirme; "Katmanlar" menusu doldurur) ---
    private bool _showProvinces = true, _showGeoLabels = true, _showFlow = true;
    public bool ShowProvinceBorders { get => _showProvinces; set { _showProvinces = value; InvalidateVisual(); } }
    public bool ShowGeoLabels { get => _showGeoLabels; set { _showGeoLabels = value; InvalidateVisual(); } }
    public bool ShowFlowArrows { get => _showFlow; set { _showFlow = value; InvalidateVisual(); } }

    // Komsu ulke / deniz / onemli sehir etiketleri (cografi baglam).
    private enum GeoKind { Sea, Country, City }
    private readonly record struct GeoLabel(string Name, double Lat, double Lon, GeoKind Kind);
    private static readonly GeoLabel[] GeoLabels =
    {
        new("K A R A D E N İ Z", 43.3, 34.5, GeoKind.Sea),
        new("A K D E N İ Z", 35.0, 31.5, GeoKind.Sea),
        new("E G E\nD E N İ Z İ", 38.4, 24.9, GeoKind.Sea),
        new("MARMARA D.", 40.6, 27.9, GeoKind.Sea),
        new("BULGARİSTAN", 42.3, 25.2, GeoKind.Country),
        new("YUNANİSTAN", 39.7, 22.2, GeoKind.Country),
        new("GÜRCİSTAN", 42.4, 43.4, GeoKind.Country),
        new("ERMENİSTAN", 40.2, 45.4, GeoKind.Country),
        new("NAHÇIVAN", 39.3, 45.5, GeoKind.Country),
        new("İRAN", 37.8, 45.9, GeoKind.Country),
        new("IRAK", 35.4, 43.9, GeoKind.Country),
        new("SURİYE", 35.1, 38.6, GeoKind.Country),
        new("RUSYA", 44.6, 37.8, GeoKind.Country),
    };

    // Adi haritada varsayilan olarak gorunen onemli sehirler (istasyon dugumleri).
    private static readonly string[] MajorCities =
        { "İstanbul", "Ankara", "İzmir", "Bursa", "Adana", "Gaziantep", "Konya", "Ceyhan" };
    private static bool IsMajorCity(PNode n) =>
        n.IsStation && Array.Exists(MajorCities, c => n.Name.StartsWith(c, StringComparison.Ordinal));

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
    private int _hoverStartTick; // hover buyume animasyonu baslangici
    private Point _mouse;

    // Secili istasyon: panel <-> harita baglantisi icin parlak halka cizilir.
    private string? _selectedId;
    public string? SelectedId
    {
        get => _selectedId;
        set { _selectedId = value; InvalidateVisual(); }
    }

    // Provins + deniz geometrisi cache (boyut/zoom/pan degisince yeniden kurulur).
    private Geometry? _provGeo;
    private Geometry? _seaGeo;
    private double _pkW, _pkH, _pkZoom = 1, _pkPanX, _pkPanY;

    // Deniz alanlari (lat/lon [lon,lat] halkalari): kara zemininden "su" oyar.
    // Kara (Turkiye) tarafi kenarlar bilerek kiyinin icine tasar ki deniz ile
    // kara arasinda bosluk kalmasin; Turkiye ustte cizildiginden fazlalik kapanir.
    // Denizler birbiriyle serbestce ortusur (BuildRoundedRingGeometry Nonzero ile birlestirir).
    private static readonly double[][][] SeaPolys =
    {
        new[]{ new[]{26.3,40.0}, new[]{43.4,40.0}, new[]{43.4,50.0}, new[]{26.3,50.0} }, // Karadeniz
        new[]{ new[]{23.2,32.5}, new[]{28.7,32.5}, new[]{28.7,41.3}, new[]{23.2,41.3} }, // Ege
        new[]{ new[]{25.8,23.0}, new[]{37.8,23.0}, new[]{37.8,37.8}, new[]{25.8,37.8} }, // Akdeniz
        new[]{ new[]{25.8,39.6}, new[]{30.4,39.6}, new[]{30.4,41.9}, new[]{25.8,41.9} }, // Marmara
    };

    // Turkiye sinirindan disari giden komsu sinir cizgileri (lat/lon [lon,lat] ciftleri).
    private static readonly double[][][] DividerLines =
    {
        new[]{ new[]{26.6,41.9}, new[]{24.6,43.8} }, // Bulgaristan | Yunanistan (KB)
        new[]{ new[]{43.5,41.2}, new[]{45.8,42.7} }, // Gurcistan | Ermenistan (KD)
        new[]{ new[]{44.6,39.5}, new[]{47.4,39.0} }, // Ermenistan | Iran (D)
        new[]{ new[]{44.4,37.6}, new[]{47.2,36.2} }, // Iran | Irak (GD)
        new[]{ new[]{42.3,37.1}, new[]{41.2,34.2} }, // Irak | Suriye (G)
        new[]{ new[]{36.6,36.5}, new[]{36.9,33.8} }, // Suriye guney (G)
    };

    // Taban projeksiyonu (zoom/pan'siz); bolge odaklama hesabi icin saklanir.
    private Func<double, double, Point>? _geoProj;
    private bool _geoReady;
    private Point GeoAt(double lat, double lon) => _geoProj!(lat, lon);

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
        // Zemin = kara (Turkiye disi her yer); denizler ustte oyulur.
        dc.DrawRectangle(new SolidColorBrush(NeighLand), null, new Rect(0, 0, w, h));
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
            const double pad = 78; // komsu deniz/ulke etiketlerine yer birak
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

        _geoProj = geoProj; _geoReady = true; // bolge odaklama icin sakla

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
                _provGeo = BuildRingGeometry(TurkeyMap.Provinces.SelectMany(p => p.Rings), project);
                // Deniz bloklari: kose yuvarlatmayla yumusatilir (dikdortgen his kalkar);
                // Turkiye sinirlari keskin kalir -> dusuk detayli kiyi korunur.
                _seaGeo = BuildRoundedRingGeometry(SeaPolys, project, 22 * _zoom);
                _pkW = w; _pkH = h; _pkZoom = _zoom; _pkPanX = _panX; _pkPanY = _panY;
            }

            // Denizler: kara zemininden su (mavi) oyar -> kiyilar belirir.
            // Once yumusak "feather": genisleyen soluk deniz halkalari, deniz ile komsu
            // karayi sert kenar yerine gradyan gecisle kaynastirir (kaynasik gorunum);
            // sonra tam dolgu. Turkiye ustte cizildiginden kendi kiyisi keskin kalir,
            // komsu-tarafi duz sinir cizgileri (DividerLines) sonra ciziip korunur.
            if (_seaGeo != null)
            {
                for (int i = 6; i >= 1; i--)
                {
                    var feather = new Pen(new SolidColorBrush(Color.FromArgb((byte)(48 - i * 6), Sea.R, Sea.G, Sea.B)),
                                          i * 9 * Math.Clamp(_zoom, 0.75, 1.8))
                        { LineJoin = PenLineJoin.Round };
                    dc.DrawGeometry(null, feather, _seaGeo);
                }
                dc.DrawGeometry(new SolidColorBrush(Sea), null, _seaGeo);
            }

            // Komsu sinir cizgileri: Turkiye'den disari giden duz cizgiler.
            if (_showProvinces)
            {
                var divPen = new Pen(new SolidColorBrush(NeighEdge), 1.1);
                foreach (var d in DividerLines)
                    dc.DrawLine(divPen, project(d[0][1], d[0][0]), project(d[1][1], d[1][0]));
            }

            // Turkiye (daha acik) ustte -> belirgin durur, denizin fazlasini kapatir.
            dc.DrawGeometry(new SolidColorBrush(Land),
                            _showProvinces ? new Pen(new SolidColorBrush(Province), 0.7) : null, _provGeo);
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_showGeoLabels) DrawGeoLabels(dc, project, dpi);
        DrawRegionLabels(dc, project, dpi);

        _screen.Clear();
        foreach (var n in nodes)
            _screen[n.Id] = project(n.Lat, n.Lon);

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

            // Boru gorunumu: dista koyu kilif, ustte ana renk, ortada ince parlak
            // sheen -> silindirik "boru" hissi. Yuvarlak uclar/birlesimler purussuz.
            var casingCol = Color.FromArgb(150, (byte)(col.R * 0.35), (byte)(col.G * 0.35), (byte)(col.B * 0.35));
            var casingPen = new Pen(new SolidColorBrush(casingCol), thick + 2.5)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var mainPen = new Pen(new SolidColorBrush(col), thick)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var sheenPen = new Pen(new SolidColorBrush(Color.FromArgb(SheenA, PipeSheen.R, PipeSheen.G, PipeSheen.B)), Math.Max(0.8, thick * 0.32))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(casingPen, pts[i], pts[i + 1]);
            for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(mainPen, pts[i], pts[i + 1]);
            for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(sheenPen, pts[i], pts[i + 1]);

            // Sizinti: yanip sonen kirmizi kalin katman.
            if (snap.Leak)
            {
                double lp = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 200.0);
                byte la = (byte)(60 + lp * 190);
                var leakPen = new Pen(new SolidColorBrush(Color.FromArgb(la, 0xE7, 0x4C, 0x3C)), thick + 7)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i + 1 < pts.Length; i++) dc.DrawLine(leakPen, pts[i], pts[i + 1]);
            }

            // Toplam guzergah uzunlugu (akis noktalari + etiket konumu icin).
            double segLen = 0;
            for (int i = 0; i + 1 < pts.Length; i++) segLen += (pts[i + 1] - pts[i]).Length;

            // Akis: borunun icinde akan soluk kisa cizgiler (yon boyunca kayar).
            // Eski parlak beyaz oklardan daha ince ve dikkat dagitmayan.
            if (_showFlow && segLen > 20)
            {
                int dashN = Math.Clamp((int)(segLen / 40), 2, 10);
                double dashLen = Math.Min(6, thick * 1.6);
                var flowPen = new Pen(new SolidColorBrush(Color.FromArgb(120, FlowDash.R, FlowDash.G, FlowDash.B)),
                    Math.Max(1.0, thick * 0.5)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < dashN; i++)
                {
                    double t = (phase + (double)i / dashN) % 1.0;
                    double d = segLen * t;
                    var a = PointAlong(pts, Math.Max(0, d - dashLen / 2), out _);
                    var b = PointAlong(pts, Math.Min(segLen, d + dashLen / 2), out _);
                    dc.DrawLine(flowPen, a, b);
                }
            }

            // Etiket: guzergahin orta noktasi, yerel yone dik yukari kaydirilir.
            var mid = PointAlong(pts, segLen / 2, out var dir);
            var perp = new Vector(-dir.Y, dir.X); if (perp.Y > 0) perp.Negate(); // etiket yukari tarafa
            var lc = new Point(mid.X + perp.X * 16, mid.Y + perp.Y * 16);
            // Akis etiketi yalniz yakinlasinca ya da uzerine gelince (kalabaligi onle).
            bool showFlow = (_zoom > 2.2 || _hoverId == seg.Id) && segLen > 90;
            if (showFlow)
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
        // Daire boyutu zoom'a bagli: tam gorunumde kucuk, yakinlastikca buyur
        // (yakin sehir dugumleri ust uste binmesin).
        double nodeScale = Math.Clamp(0.55 + 0.42 * (_zoom - 1), 0.4, 1.7);

        // Etiket kalabaligini onle: varsayilan olarak yalniz secili/hover; yeterince
        // yakinlasinca istasyon adlari da gorunur.
        bool zoomLabels = _zoom > 2.2;

        var labelDraws = new List<Action>(); // etiketler tum dairelerden SONRA (ustte kalir)
        foreach (var n in nodes)
        {
            if (IsHidden(n)) continue; // kategori gizli: daire + etiket cizilmez
            var p = _screen[n.Id];
            var snap = _source.Node(n.Id);
            bool hovered = n.Id == _hoverId && !_hoverSeg;
            bool dimmed = _activeRegion != null && n.Region != _activeRegion; // izolasyon
            double r = (n.IsStation ? 12 : 8) * nodeScale;

            // Hover: 120ms'de yumusak %20 buyume.
            if (hovered)
            {
                double ht = Math.Clamp((Environment.TickCount - _hoverStartTick) / 120.0, 0, 1);
                r *= 1 + 0.20 * (ht * ht * (3 - 2 * ht)); // smoothstep
            }

            if (dimmed) dc.PushOpacity(0.22);
            DrawNodeGlyph(dc, n, p, r, snap, nodeScale);
            if (dimmed) dc.Pop();

            // Secili istasyon: parlak halka — detay paneliyle gorsel bag.
            if (n.Id == _selectedId)
            {
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(60, TextCol.R, TextCol.G, TextCol.B)), 5), p, r + 7, r + 7);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(235, TextCol.R, TextCol.G, TextCol.B)), 1.6), p, r + 5, r + 5);
            }

            // Etiket yalniz: secili, hover, onemli sehir, ya da yakinlasinca (istasyonlar).
            bool showLabel = !dimmed && (n.Id == _selectedId || hovered || IsMajorCity(n) || (zoomLabels && n.IsStation));
            if (showLabel)
            {
                var nc = n; var pc = p; var rc = r;
                labelDraws.Add(() =>
                {
                    var name = Text(nc.Name, 11.5, TextCol, dpi);
                    DrawLabel(dc, name, new Point(pc.X - name.Width / 2, pc.Y + rc + 5));
                });
            }
        }

        // Etiket gecisi: hicbir daire etiketin ustune binmez.
        foreach (var draw in labelDraws) draw();

        DrawTooltip(dc, dpi);
    }

    // Dugum turune gore ayri gorsel: istasyon=daire, depo=silindir, sinir=elmas,
    // cikis=asagi ucgen, kavsak/diger=ici bos halka. Boylece tur bir bakista okunur.
    private void DrawNodeGlyph(DrawingContext dc, PNode n, Point p, double r, NodeSnap snap, double nodeScale)
    {
        var bgPen = new Pen(new SolidColorBrush(Bg), Math.Max(1, 2 * nodeScale));

        if (n.IsStation)
        {
            Brush fill = HealthBrush(snap.Health);
            var c = ((SolidColorBrush)fill).Color;

            if (snap.Health < 20) // kritik: genis isi haresi + yanip sonen halka
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(24, 0xE7, 0x4C, 0x3C)), null, p, r + 26, r + 26);
                double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 300.0);
                byte a = (byte)(40 + pulse * 190);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(a, 0xE7, 0x4C, 0x3C)), 3), p, r + 10, r + 10);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)), null, p, r + 6, r + 6);
            dc.DrawEllipse(fill, bgPen, p, r, r);
            return;
        }

        var col = NodeTypeColor(n.Type);
        var colBrush = new SolidColorBrush(col);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(55, col.R, col.G, col.B)), null, p, r + 5, r + 5);

        switch (n.Type)
        {
            case "STORAGE":
            {
                // Silindir (depo): govde + doluluk + ust elips.
                double rw = r * 1.3, rh = r * 1.7, ry = r * 0.42;
                var body = new Rect(p.X - rw, p.Y - rh + ry, rw * 2, rh * 2 - ry * 2);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, Bg.R, Bg.G, Bg.B)), null, body);
                double lvl = Math.Clamp(_source?.Level(n.Id) ?? 0, 0, 100);
                double fh = body.Height * lvl / 100.0;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, col.R, col.G, col.B)), null,
                    new Rect(body.X, body.Bottom - fh, body.Width, fh));
                var pen = new Pen(colBrush, Math.Max(1, 1.6 * nodeScale));
                dc.DrawRectangle(null, pen, body);
                dc.DrawEllipse(new SolidColorBrush(col), pen, new Point(p.X, body.Top), rw, ry);
                break;
            }
            case "BORDER":
            {
                // Elmas (sinir gecisi).
                double d = r * 1.25;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(p.X, p.Y - d), true, true);
                    ctx.LineTo(new Point(p.X + d, p.Y), true, false);
                    ctx.LineTo(new Point(p.X, p.Y + d), true, false);
                    ctx.LineTo(new Point(p.X - d, p.Y), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(colBrush, bgPen, g);
                break;
            }
            case "OFFTAKE":
            {
                // Asagi ucgen (sehir cikisi: gaz disari).
                double d = r * 1.15;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(p.X - d, p.Y - d * 0.7), true, true);
                    ctx.LineTo(new Point(p.X + d, p.Y - d * 0.7), true, false);
                    ctx.LineTo(new Point(p.X, p.Y + d), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(colBrush, bgPen, g);
                break;
            }
            default: // JUNCTION / LNG / FSRU / TERMINAL: ici bos halka
                dc.DrawEllipse(new SolidColorBrush(Bg), new Pen(colBrush, Math.Max(1.5, 2.4 * nodeScale)), p, r, r);
                break;
        }
    }

    // Aktif (izole) bolgenin adini merkeze parlak cizer. Izolasyon yokken
    // bolge adlari cizilmez (deniz/ulke etiketleriyle cakismasin).
    private void DrawRegionLabels(DrawingContext dc, Func<double, double, Point> project, double dpi)
    {
        if (_activeRegion == null) return;
        var grp = PipelineTopology.Nodes.Where(n => n.Region == _activeRegion).ToList();
        if (grp.Count == 0) return;
        var p = project(grp.Average(n => n.Lat), grp.Average(n => n.Lon));
        var ft = Text(RegionAd(_activeRegion).ToUpperInvariant(), 15,
            Color.FromArgb(210, 0x4E, 0xCD, 0xC4), dpi);
        dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
    }

    // Deniz / komsu ulke etiketlerini cizer (soluk, arka planda baglam).
    private void DrawGeoLabels(DrawingContext dc, Func<double, double, Point> project, double dpi)
    {
        foreach (var g in GeoLabels)
        {
            var p = project(g.Lat, g.Lon);
            if (p.X < -60 || p.Y < -40 || p.X > ActualWidth + 60 || p.Y > ActualHeight + 40) continue;

            (double size, Color col) = g.Kind switch
            {
                GeoKind.Sea => (12.5, Color.FromArgb(160, GeoSeaCol.R, GeoSeaCol.G, GeoSeaCol.B)),
                GeoKind.Country => (11.0, Color.FromArgb(140, GeoCountryCol.R, GeoCountryCol.G, GeoCountryCol.B)),
                _ => (10.5, Color.FromArgb(150, MutedCol.R, MutedCol.G, MutedCol.B)),
            };
            foreach (var (line, idx) in g.Name.Split('\n').Select((l, i) => (l, i)))
            {
                var ft = Text(line, size, col, dpi);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2 + idx * (ft.Height + 1)));
            }
        }
    }

    // Verilen halkalari (her biri [lon,lat] noktali) tek dolgulu geometriye cevirir.
    private static Geometry BuildRingGeometry(IEnumerable<double[][]> rings, Func<double, double, Point> project)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var ring in rings)
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

    // BuildRingGeometry gibi ama her koseyi 'radius' piksel yaricapinda kavise cevirir
    // (quadratik bezier). Blok/dikdortgen denizleri yumusatir; komsu-tarafi duz kenarlar
    // kalir ama sert 90° koseler kaybolur. Turkiye sinirlari icin kullanilmaz.
    private static Geometry BuildRoundedRingGeometry(IEnumerable<double[][]> rings, Func<double, double, Point> project, double radius)
    {
        // Nonzero: ust uste binen deniz halkalari delik acmadan birlesir.
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            foreach (var ring in rings)
            {
                if (ring.Length < 3) continue;
                int n = ring.Length;
                var pts = new Point[n];
                for (int i = 0; i < n; i++) pts[i] = project(ring[i][1], ring[i][0]);

                // Her kose icin giris (onceki kenardan) ve cikis (sonraki kenara) noktasi;
                // yaricap komsu kenarin yarisini asamaz (kisa kenarda tasmayi onler).
                var starts = new Point[n];
                var ends = new Point[n];
                for (int i = 0; i < n; i++)
                {
                    var cur = pts[i];
                    var toPrev = pts[(i - 1 + n) % n] - cur;
                    var toNext = pts[(i + 1) % n] - cur;
                    double lp = Math.Max(1e-6, toPrev.Length), ln = Math.Max(1e-6, toNext.Length);
                    starts[i] = cur + toPrev * (Math.Min(radius, lp / 2) / lp);
                    ends[i] = cur + toNext * (Math.Min(radius, ln / 2) / ln);
                }

                ctx.BeginFigure(ends[0], true, true);
                for (int i = 1; i <= n; i++)
                {
                    int idx = i % n;
                    ctx.LineTo(starts[idx], true, false);          // duz kenar
                    ctx.QuadraticBezierTo(pts[idx], ends[idx], true, false); // yumusak kose
                }
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
        string? prevHover = _hoverId;
        _hoverId = null;
        foreach (var n in PipelineTopology.Nodes)
            if (!IsHidden(n) && _screen.TryGetValue(n.Id, out var p) && (_mouse - p).Length <= 18)
            {
                _hoverId = n.Id; _hoverSeg = false;
                if (_hoverId != prevHover) _hoverStartTick = Environment.TickCount;
                Cursor = Cursors.Hand; // tiklanabilir gostergesi
                InvalidateVisual();
                return;
            }
        foreach (var s in PipelineTopology.Segments)
            if (_segScreen.TryGetValue(s.Id, out var pts) && DistToPolyline(_mouse, pts) <= 7)
            { _hoverId = s.Id; _hoverSeg = true; Cursor = Cursors.Hand; InvalidateVisual(); return; }

        Cursor = Cursors.Arrow; // bos alan
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverId = null; Cursor = Cursors.Arrow; InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
        // Alt sinir 0.45: komsu ulkeleri/denizleri gormek icin daha genis uzaklas.
        double nz = Math.Clamp(_zoom * f, 0.45, 8.0);
        f = nz / _zoom;
        var m = e.GetPosition(this);
        _panX = m.X - (m.X - _panX) * f;   // imlec altindaki nokta sabit kalir
        _panY = m.Y - (m.Y - _panY) * f;
        _zoom = nz;
        InvalidateVisual();
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        _zoom = 1; _panX = 0; _panY = 0; // tam sigdir
        InvalidateVisual();
    }

    /// <summary>Verilen cografi kutuya yumusakca odaklan (bolge izolasyonu).</summary>
    public void FocusBounds(double minLat, double maxLat, double minLon, double maxLon, double marginFrac = 0.25)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40 || !_geoReady) return;

        // Kutunun tam-gorunum (zoom=1) ekran koordinatlari.
        var a = GeoAt(maxLat, minLon); var b = GeoAt(minLat, maxLon);
        double bw = Math.Abs(b.X - a.X), bh = Math.Abs(b.Y - a.Y);
        if (bw < 1 || bh < 1) return;

        double target = Math.Clamp(Math.Min(w / (bw * (1 + marginFrac)), h / (bh * (1 + marginFrac))), 0.45, 8.0);
        double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2;
        _zoom = target;
        _panX = w / 2 - cx * target;
        _panY = h / 2 - cy * target;
        InvalidateVisual();
    }

    /// <summary>Tam Turkiye gorunumune don.</summary>
    public void ResetView() { _activeRegion = null; _zoom = 1; _panX = 0; _panY = 0; InvalidateVisual(); }

    // Aktif (izole) bolge: digerleri soluklasir, o bolgeye odaklanilir.
    private string? _activeRegion;

    // Bolge anahtari -> Turkce ad (UI ve etiketler icin).
    public static readonly (string Key, string Ad)[] Regions =
    {
        ("marmara", "Marmara"), ("ege", "Ege"), ("akdeniz", "Akdeniz"),
        ("ic_anadolu", "İç Anadolu"), ("orta_anadolu", "Orta Anadolu"),
        ("karadeniz", "Karadeniz"), ("dogu_anadolu", "Doğu Anadolu"),
        ("guneydogu_anadolu", "Güneydoğu Anadolu"),
    };

    private static string RegionAd(string key)
    {
        foreach (var (k, ad) in Regions) if (k == key) return ad;
        return key;
    }

    /// <summary>Bir bolgeyi izole et: o bolgenin dugumlerine odaklan, digerlerini soluklastir.</summary>
    public void FocusRegion(string regionKey)
    {
        var pts = PipelineTopology.Nodes.Where(n => n.Region == regionKey).ToList();
        if (pts.Count == 0) return;
        _activeRegion = regionKey;
        FocusBounds(pts.Min(n => n.Lat), pts.Max(n => n.Lat),
                    pts.Min(n => n.Lon), pts.Max(n => n.Lon), 0.35);
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

    private static readonly Color AccentCol = Color.FromRgb(0x4E, 0xCD, 0xC4);

    // Yeniden tasarlanan ipucu kutusu: baslik (renkli nokta + ad), altta ayrinti
    // satirlari; yuvarlak koseli, golgeli, imlecin ustune tasmayan konumda.
    private void DrawTooltip(DrawingContext dc, double dpi)
    {
        if (_hoverId == null || _source == null) return;

        Color dot; string title, sub; string[] rows;
        if (_hoverSeg)
        {
            var s = PipelineTopology.Segments.First(x => x.Id == _hoverId);
            var snap = _source.Segment(s.Id);
            dot = Lerp(AccentCol, Color.FromRgb(0xFF, 0x6B, 0x6B), snap.LoadRatio);
            title = s.Id; sub = $"{s.From} → {s.To}";
            rows = snap.Leak
                ? new[] { $"Akış: {snap.FlowMcmDay:0} mcm/gün", "⚠ SIZINTI" }
                : new[] { $"Akış: {snap.FlowMcmDay:0} mcm/gün" };
        }
        else
        {
            var n = PipelineTopology.NodeById(_hoverId);
            title = n.Name; sub = n.Id;
            if (n.IsStation)
            {
                var snap = _source.Node(n.Id);
                dot = ((SolidColorBrush)HealthBrush(snap.Health)).Color;
                string durum = snap.Health >= 70 ? "Sağlıklı" : snap.Health >= 40 ? "Uyarı"
                             : snap.Health >= 20 ? "Riskli" : "Kritik";
                rows = new[] { $"Durum: {durum}   %{snap.Health:0}", $"RUL: {snap.Rul:0} döngü" };
            }
            else if (n.Type == "STORAGE")
            { dot = NodeTypeColor(n.Type); rows = new[] { $"Depo doluluk: %{_source.Level(n.Id):0}" }; }
            else
            { dot = NodeTypeColor(n.Type); rows = new[] { TypeLabel(n.Type) }; }
        }

        var titleFt = Text(title, 12.5, TextCol, dpi); titleFt.SetFontWeight(FontWeights.SemiBold);
        var subFt = Text(sub, 10, MutedCol, dpi);
        var rowFts = rows.Select(r => Text(r, 11, r.StartsWith("⚠") ? Color.FromRgb(0xE7, 0x4C, 0x3C) : MutedCol, dpi)).ToList();

        double pad = 10, dotW = 14, gap = 4;
        double contentW = Math.Max(dotW + titleFt.Width, subFt.Width);
        foreach (var rf in rowFts) contentW = Math.Max(contentW, rf.Width);
        double w = contentW + 2 * pad;
        double h = pad + titleFt.Height + gap + subFt.Height + 6 + rowFts.Sum(r => r.Height + 2) + pad;

        double x = _mouse.X + 16, y = _mouse.Y + 16;
        if (x + w > ActualWidth - 4) x = _mouse.X - w - 16;
        if (y + h > ActualHeight - 4) y = ActualHeight - h - 4;
        var rect = new Rect(x, y, w, h);

        // Golge + govde (yuvarlak kose).
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
            new Rect(x + 2, y + 3, w, h), 8, 8);
        dc.DrawRoundedRectangle(new SolidColorBrush(TipBg),
            new Pen(new SolidColorBrush(TipBorder), 1), rect, 8, 8);

        double cy = y + pad;
        dc.DrawEllipse(new SolidColorBrush(dot), null, new Point(x + pad + 5, cy + titleFt.Height / 2), 5, 5);
        dc.DrawText(titleFt, new Point(x + pad + dotW, cy));
        cy += titleFt.Height + gap;
        dc.DrawText(subFt, new Point(x + pad, cy));
        cy += subFt.Height + 6;
        foreach (var rf in rowFts) { dc.DrawText(rf, new Point(x + pad, cy)); cy += rf.Height + 2; }
    }

    private static string TypeLabel(string type) => type switch
    {
        "BORDER" => "Sınır İstasyonu",
        "OFFTAKE" => "Şehir Gaz Çıkışı",
        "JUNCTION" => "Dağıtım Kavşağı",
        "STORAGE" => "Yeraltı Depolama",
        "LNG" => "LNG Terminali",
        "FSRU" => "FSRU",
        "TERMINAL" => "Geçiş İstasyonu",
        _ => type,
    };
}
