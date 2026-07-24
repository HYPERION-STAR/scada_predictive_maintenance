using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScadaClient.Parsing;
using ScadaClient.Services;

namespace ScadaDashboard.Pipeline;

/// <summary>
/// Topolojiyi canlı API'den kurar. <c>/nodes</c> health_state yüzünden 500 verse bile
/// çalışır: düğümler <c>/segments</c> uçlarından + geometriden sentezlenir;
/// üniteler <c>/live_data</c> anahtarlarından (CS-…-U#) ve isteğe bağlı <c>/node/{id}</c>'den.
/// Telemetri buradan doldurulmaz; <see cref="LiveSnapshotSource"/> tazeler.
/// </summary>
public static class ApiSnapshotLoader
{
    private static readonly Regex GasUnitIdRx = new(
        @"^(?<base>[A-Z]+-[A-Z0-9]+)-U\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Petrol pompa ünitesi: PT-SIVAS-P1 → istasyon PT-SIVAS-01.</summary>
    private static readonly Regex OilPumpIdRx = new(
        @"^(?<base>(?:PS|PT)-[A-Z0-9]+)-P\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SnapshotData Load(string baseUrl)
    {
        baseUrl = baseUrl.TrimEnd('/');
        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(12),
        };

        var segsJson = http.GetStringAsync("/api/scada/segments").GetAwaiter().GetResult();
        using var segsDoc = JsonDocument.Parse(segsJson);

        // --- Segmentler (önce; /nodes kırık olsa bile topoloji kurulur) ---
        var segments = new List<PSegment>();
        var segSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segsDoc.RootElement.EnumerateArray())
        {
            string id = Str(s, "segment_id");
            if (id.Length == 0 || !segSeen.Add(id)) continue;

            List<GeoPoint>? geometry = null;
            if (s.TryGetProperty("geometry_polyline", out var poly)
                && poly.ValueKind == JsonValueKind.Array && poly.GetArrayLength() >= 2)
            {
                geometry = new List<GeoPoint>();
                foreach (var pt in poly.EnumerateArray())
                    geometry.Add(new GeoPoint(pt[0].GetDouble(), pt[1].GetDouble()));
            }

            segments.Add(new PSegment(
                id,
                Str(s, "from_node"),
                Str(s, "to_node"),
                Str(s, "product", "gas"),
                Num(s, "max_capacity_mcm_day"),
                geometry));
        }

        // --- Düğümler: /nodes dene; 500/boşsa segment uçlarından sentezle ---
        var nodes = TryLoadNodes(http) ?? SynthesizeNodesFromSegments(segments);
        TopologyReconciler.EnsureSegmentEndpoints(nodes, segments);

        // --- İstasyon üniteleri: live_data anahtarları + isteğe bağlı /node/{id} ---
        var stationUnits = BuildStationUnits(http, nodes);

        for (int i = 0; i < nodes.Count; i++)
        {
            // Gaz CS + petrol PS/PT: ünite varsa haritada pasta/sağlık dairesi (IsStation).
            if (stationUnits.ContainsKey(nodes[i].Id)
                && nodes[i].Type is "CS" or "PS" or "PT")
                nodes[i] = nodes[i] with { IsStation = true };
        }

        return new SnapshotData
        {
            Nodes = nodes,
            Segments = segments,
            Telemetry = new Dictionary<string, IReadOnlyDictionary<string, double>>(),
            StationUnits = stationUnits,
        };
    }

    private static List<PNode>? TryLoadNodes(HttpClient http)
    {
        try
        {
            var nodesJson = http.GetStringAsync("/api/scada/nodes").GetAwaiter().GetResult();
            using var nodesDoc = JsonDocument.Parse(nodesJson);
            if (nodesDoc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var nodes = new List<PNode>();
            foreach (var n in nodesDoc.RootElement.EnumerateArray())
            {
                string id = Str(n, "node_id");
                if (id.Length == 0) continue;
                string type = InferType(id);
                nodes.Add(new PNode(
                    id,
                    Str(n, "name", id),
                    type,
                    Num(n, "lat"),
                    Num(n, "lon"),
                    false,
                    Str(n, "region")));
            }
            return nodes.Count > 0 ? nodes : null;
        }
        catch
        {
            return null; // 500 / ağ — segment sentezine düş
        }
    }

    /// <summary>
    /// /nodes yokken: her segment ucu için geometri ilk/son noktasından düğüm üret.
    /// </summary>
    private static List<PNode> SynthesizeNodesFromSegments(List<PSegment> segments)
    {
        var byId = new Dictionary<string, PNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in segments)
        {
            EnsureSynth(byId, s.From, s.Geometry, atStart: true);
            EnsureSynth(byId, s.To, s.Geometry, atStart: false);
        }
        return byId.Values.ToList();
    }

    private static void EnsureSynth(
        Dictionary<string, PNode> byId, string id, IReadOnlyList<GeoPoint>? geo, bool atStart)
    {
        if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id)) return;
        double lat = 0, lon = 0;
        if (geo is { Count: >= 1 })
        {
            var pt = atStart ? geo[0] : geo[^1];
            lat = pt.Lat;
            lon = pt.Lon;
        }
        string type = InferType(id);
        byId[id] = new PNode(id, FriendlyName(id, type), type, lat, lon, false);
    }

    private static Dictionary<string, IReadOnlyList<SnapshotUnit>> BuildStationUnits(
        HttpClient http, List<PNode> nodes)
    {
        var stationUnits = new Dictionary<string, List<SnapshotUnit>>(StringComparer.OrdinalIgnoreCase);
        var csNodes = nodes.Where(n => n.Type == "CS").ToList();
        var oilNodes = nodes.Where(n => n.Type is "PS" or "PT").ToList();

        // 1) live_data anahtarlarından ünite keşfi (gaz U# + petrol P#)
        try
        {
            var liveJson = http.GetStringAsync("/api/scada/live_data").GetAwaiter().GetResult();
            using var liveDoc = JsonDocument.Parse(liveJson);
            if (liveDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in liveDoc.RootElement.EnumerateObject())
                {
                    string unitId = prop.Name;
                    string entityType = "";
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("entity_type", out var et))
                        entityType = et.GetString() ?? "";

                    var gas = GasUnitIdRx.Match(unitId);
                    if (gas.Success)
                    {
                        string baseId = gas.Groups["base"].Value;
                        var station = csNodes.FirstOrDefault(n =>
                            n.Id.StartsWith(baseId + "-", StringComparison.OrdinalIgnoreCase)
                            || n.Id.Equals(baseId, StringComparison.OrdinalIgnoreCase));
                        if (station == null) continue;
                        AddUnit(stationUnits, station.Id, unitId,
                            entityType.Length > 0 ? entityType : "compressor");
                        continue;
                    }

                    var oil = OilPumpIdRx.Match(unitId);
                    if (oil.Success)
                    {
                        string baseId = oil.Groups["base"].Value;
                        var station = oilNodes.FirstOrDefault(n =>
                            n.Id.StartsWith(baseId + "-", StringComparison.OrdinalIgnoreCase)
                            || n.Id.Equals(baseId, StringComparison.OrdinalIgnoreCase));
                        if (station == null) continue;
                        AddUnit(stationUnits, station.Id, unitId,
                            entityType.Length > 0 ? entityType : "oil_pump");
                    }
                }
            }
        }
        catch { /* live_data yoksa /node dene */ }

        // 2) Eksik gaz istasyonları için /node/{id}
        using var api = new ScadaApiClient(http, new ScadaJsonParser());
        var missing = csNodes.Where(n => !stationUnits.ContainsKey(n.Id)).Select(n => n.Id).ToList();
        missing.AddRange(oilNodes.Where(n => !stationUnits.ContainsKey(n.Id)).Select(n => n.Id));
        if (missing.Count > 0)
        {
            var tasks = missing.Select(id => FetchUnitsAsync(api, id)).ToArray();
            var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
            foreach (var (id, units) in results)
                if (units.Count > 0)
                    stationUnits[id] = units.ToList();
        }

        return stationUnits.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<SnapshotUnit>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddUnit(
        Dictionary<string, List<SnapshotUnit>> map, string stationId, string unitId, string type)
    {
        if (!map.TryGetValue(stationId, out var list))
            map[stationId] = list = new List<SnapshotUnit>();
        if (list.All(u => !u.Id.Equals(unitId, StringComparison.OrdinalIgnoreCase)))
            list.Add(new SnapshotUnit(unitId, "", type));
    }

    private static async Task<(string Id, IReadOnlyList<SnapshotUnit> Units)> FetchUnitsAsync(
        ScadaApiClient api, string id)
    {
        try
        {
            var detail = await api.GetNodeAsync(id).ConfigureAwait(false);
            var list = new List<SnapshotUnit>();
            foreach (var u in detail.LiveEquipment)
            {
                string uid = u.TryGetProperty("entity_id", out var e) ? e.GetString() ?? "" : "";
                if (uid.Length == 0) continue;
                string utype = u.TryGetProperty("entity_type", out var et) ? et.GetString() ?? "" : "";
                list.Add(new SnapshotUnit(uid, "", utype));
            }
            return (id, list);
        }
        catch { return (id, Array.Empty<SnapshotUnit>()); }
    }

    internal static string InferType(string id)
    {
        int dash = id.IndexOf('-');
        string prefix = dash > 0 ? id.Substring(0, dash) : id;
        return prefix switch
        {
            "UGS" => "STORAGE",
            "CS" => "CS",
            "BORDER" => "BORDER",
            "OFFTAKE" => "OFFTAKE",
            "JUNCTION" => "JUNCTION",
            _ => prefix,
        };
    }

    private static string FriendlyName(string id, string type)
    {
        var parts = id.Split('-');
        string token = parts.Length >= 2 ? parts[1] : id;
        string pretty = System.Globalization.CultureInfo.CurrentCulture.TextInfo
            .ToTitleCase(token.ToLowerInvariant());
        return type switch
        {
            "BORDER" => $"{pretty} Sınır",
            "OFFTAKE" => $"{pretty} Çıkış",
            "CS" => $"{pretty} KS",
            "STORAGE" => $"{pretty} Depo",
            _ => pretty,
        };
    }

    private static string Str(JsonElement obj, string prop, string fallback = "") =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static double Num(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
