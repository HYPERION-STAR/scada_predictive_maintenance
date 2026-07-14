namespace ScadaDashboard.Pipeline;

/// <summary>Harita düğümü (istasyon / sınır / çıkış / kavşak).</summary>
public sealed record PNode(string Id, string Name, string Type, double Lat, double Lon, bool IsStation);

/// <summary>İki düğüm arası boru parçası (hat).</summary>
public sealed record PSegment(string Id, string From, string To, string Product);

/// <summary>
/// Canlı harita için statik topoloji (Kişi 3 UI tarafında, kendine yeten).
/// Jenerik / bağımsız — gerçek kurum adı içermez. Koordinatlar yaklaşık,
/// yalnızca haritanın tanınabilir bir düzende çizilmesi için.
/// </summary>
public static class PipelineTopology
{
    public static readonly IReadOnlyList<PNode> Nodes = new List<PNode>
    {
        new("N1", "Kuzey Giris",        "BORDER",   41.9, 28.0, false),
        new("N2", "Trakya KS",          "CS",       41.0, 28.9, true),
        new("N3", "Ic Anadolu Kavsagi", "JUNCTION", 39.9, 32.8, false),
        new("N4", "Sivas KS",           "CS",       39.7, 37.0, true),
        new("N5", "Dogu Cikis",         "OFFTAKE",  39.9, 41.3, false),
        new("N6", "Bati Cikis",         "OFFTAKE",  38.4, 27.1, false),
        new("N7", "Dogu Giris",         "BORDER",   39.7, 44.0, false),
        new("N8", "Marmara Deposu",     "STORAGE",  40.5, 27.5, false),
    };

    public static readonly IReadOnlyList<PSegment> Segments = new List<PSegment>
    {
        new("S1", "N1", "N2", "gas"),
        new("S2", "N2", "N3", "gas"),
        new("S3", "N3", "N6", "gas"),
        new("S4", "N3", "N4", "gas"),
        new("S5", "N4", "N5", "gas"),
        new("S6", "N7", "N4", "gas"),
        new("S7", "N2", "N8", "gas"),
    };

    public static PNode NodeById(string id) => Nodes.First(n => n.Id == id);
}
