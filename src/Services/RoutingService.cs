using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Roteamento de circuitos pela infraestrutura modelada (eletrodutos, eletrocalhas e conexões).
    /// Monta um grafo pelos conectores, liga o quadro e cada dispositivo ao ponto de infraestrutura mais próximo
    /// e calcula, para cada circuito, a árvore de caminhos do quadro até os dispositivos (Dijkstra).
    /// </summary>
    public class RoutingService
    {
        public class CircuitRoute
        {
            public ElectricalSystem Circuit;
            public HashSet<ElementId> Segments = new HashSet<ElementId>();
            public double LengthM;
            public int MaxGrouping;
            /// <summary>Menor fator de agrupamento ao longo do caminho (tabela pelo tipo de cada trecho).</summary>
            public double WorstFca = 1.0;
            public int DevicesReached;
            public int DevicesTotal;
            public HashSet<ElementId> ReachedDevices = new HashSet<ElementId>();
            public bool PanelReached;
            public bool Routed => PanelReached && DevicesReached > 0;
        }

        private static readonly BuiltInCategory[] InfraCategories =
        {
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        };

        private readonly Document _doc;
        private readonly Dictionary<ElementId, List<ElementId>> _edges = new Dictionary<ElementId, List<ElementId>>();
        private readonly Dictionary<ElementId, double> _lengthFt = new Dictionary<ElementId, double>();
        private readonly List<(ElementId Node, XYZ Point)> _connectorPoints = new List<(ElementId, XYZ)>();
        private readonly double _maxEntryFt;
        private readonly Dictionary<ElementId, GroupingKind> _kind = new Dictionary<ElementId, GroupingKind>();

        public Dictionary<ElementId, HashSet<ElementId>> CircuitsBySegment { get; } = new Dictionary<ElementId, HashSet<ElementId>>();
        public List<CircuitRoute> Routes { get; } = new List<CircuitRoute>();
        public IEnumerable<ElementId> Segments => _edges.Keys;

        /// <summary>Rotas escolhidas pelo Ajustar Rota (UniqueId do circuito → UniqueIds dos trechos).</summary>
        public Dictionary<string, List<string>> StoredRoutes { get; set; }

        /// <summary>Usar as rotas guardadas no projeto (padrão).</summary>
        public bool UseStoredRoutes { get; set; } = true;

        public const string StoredKey = "rotas";

        public RoutingService(Document doc, double maxEntryDistanceM = 2.0)
        {
            _doc = doc;
            _maxEntryFt = RevitHelpers.M(maxEntryDistanceM);
            BuildGraph();
            StoredRoutes = LoadStored(doc);
        }

        public static Dictionary<string, List<string>> LoadStored(Document doc)
        {
            string json = ProjectStorage.GetExtra(doc, StoredKey);
            if (json == null) return new Dictionary<string, List<string>>();
            try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new Dictionary<string, List<string>>(); }
            catch (Exception) { return new Dictionary<string, List<string>>(); }
        }

        /// <summary>Grava as rotas guardadas (precisa de transação).</summary>
        public static void SaveStored(Document doc, Dictionary<string, List<string>> routes) =>
            ProjectStorage.SetExtra(doc, StoredKey, routes == null || routes.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(routes));

        private static ConnectorManager ConnectorsOf(Element e) =>
            e is MEPCurve curve ? curve.ConnectorManager : (e as FamilyInstance)?.MEPModel?.ConnectorManager;

        private void BuildGraph()
        {
            List<Element> infra = new FilteredElementCollector(_doc)
                .WherePasses(new ElementMulticategoryFilter(InfraCategories))
                .WhereElementIsNotElementType()
                .ToElements().ToList();

            foreach (Element e in infra)
            {
                _edges[e.Id] = new List<ElementId>();
                _lengthFt[e.Id] = e.Location is LocationCurve lc ? lc.Curve.Length : 0.0;
                _kind[e.Id] = KindOf(e);
            }

            foreach (Element e in infra)
            {
                ConnectorManager cm = ConnectorsOf(e);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType == ConnectorType.Logical) continue;
                    _connectorPoints.Add((e.Id, c.Origin));
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        ElementId other = r.Owner?.Id;
                        if (other == null || other == e.Id || !_edges.ContainsKey(other)) continue;
                        if (!_edges[e.Id].Contains(other)) _edges[e.Id].Add(other);
                        if (!_edges[other].Contains(e.Id)) _edges[other].Add(e.Id);
                    }
                }
            }
        }

        /// <summary>Elemento de infraestrutura mais próximo do ponto (e a distância, em pés).</summary>
        private (ElementId Node, double Distance) Entry(Element e)
        {
            XYZ p = RevitHelpers.PointOf(e);
            if (p == null || _connectorPoints.Count == 0) return (null, 0);
            var best = _connectorPoints.Select(cp => (cp.Node, d: cp.Point.DistanceTo(p))).OrderBy(x => x.d).First();
            return best.d <= _maxEntryFt ? (best.Node, best.d) : (null, 0);
        }

        /// <summary>Dijkstra a partir de um nó: distância e predecessor de cada nó alcançado.</summary>
        private Dictionary<ElementId, ElementId> ShortestTree(ElementId start, Func<ElementId, double> weight = null)
        {
            var dist = new Dictionary<ElementId, double> { [start] = 0 };
            var prev = new Dictionary<ElementId, ElementId> { [start] = null };
            var queue = new PriorityQueue<ElementId, double>();
            queue.Enqueue(start, 0);
            while (queue.TryDequeue(out ElementId node, out double d))
            {
                if (d > dist[node] + 1e-9) continue;
                foreach (ElementId next in _edges[node])
                {
                    double nd = d + (_lengthFt[node] + _lengthFt[next]) / 2 * (weight?.Invoke(next) ?? 1.0);
                    if (dist.TryGetValue(next, out double old) && old <= nd) continue;
                    dist[next] = nd;
                    prev[next] = node;
                    queue.Enqueue(next, nd);
                }
            }
            return prev;
        }

        public void RouteCircuits(IEnumerable<ElectricalSystem> circuits)
        {
            var treeCache = new Dictionary<ElementId, Dictionary<ElementId, ElementId>>();

            foreach (ElectricalSystem c in circuits)
            {
                var route = new CircuitRoute { Circuit = c };
                List<Element> devices = c.Elements.Cast<Element>().Select(e => _doc.GetElement(e.Id)).Where(e => e != null).ToList();
                route.DevicesTotal = devices.Count;
                Routes.Add(route);

                Element panel = c.BaseEquipment;
                if (panel == null) continue;
                var (panelNode, panelDrop) = Entry(panel);
                if (panelNode == null) continue;
                route.PanelReached = true;

                HashSet<ElementId> stored = UseStoredRoutes ? Stored(c) : null;
                Dictionary<ElementId, ElementId> tree = null;
                if (stored == null && !treeCache.TryGetValue(panelNode, out tree))
                    treeCache[panelNode] = tree = ShortestTree(panelNode);

                double dropsFt = panelDrop;
                foreach (Element device in devices)
                {
                    var (node, drop) = Entry(device);
                    if (node == null) continue;
                    if (stored != null)
                    {
                        if (!stored.Contains(node)) continue;
                        route.DevicesReached++;
                        route.ReachedDevices.Add(device.Id);
                        dropsFt += drop;
                        continue;
                    }
                    if (!tree.ContainsKey(node)) continue;
                    route.DevicesReached++;
                    route.ReachedDevices.Add(device.Id);
                    dropsFt += drop;
                    for (ElementId n = node; n != null; n = tree[n]) route.Segments.Add(n);
                }
                if (stored != null) route.Segments.UnionWith(stored);

                if (route.DevicesReached == 0) continue;
                AddRoute(route, dropsFt);
            }

            DimensioningSettings settings = DimensioningSettings.Load();
            foreach (CircuitRoute r in Routes.Where(x => x.Routed))
            {
                List<ElementId> curves = r.Segments.Where(IsCurve).ToList();
                r.MaxGrouping = curves.Select(s => CircuitsBySegment[s].Count).DefaultIfEmpty(1).Max();
                r.WorstFca = curves.Select(s => settings.GroupingFactor(_kind[s], CircuitsBySegment[s].Count)).DefaultIfEmpty(1.0).Min();
            }
        }


        private void AddRoute(CircuitRoute route, double dropsFt)
        {
            route.LengthM = RevitHelpers.ToM(route.Segments.Sum(s => _lengthFt.TryGetValue(s, out double l) ? l : 0) + dropsFt);
            foreach (ElementId s in route.Segments)
            {
                if (!CircuitsBySegment.TryGetValue(s, out var set)) CircuitsBySegment[s] = set = new HashSet<ElementId>();
                set.Add(route.Circuit.Id);
            }
        }

        /// <summary>Rota guardada do circuito, se todos os trechos ainda existirem.</summary>
        private HashSet<ElementId> Stored(ElectricalSystem c)
        {
            if (StoredRoutes == null || !StoredRoutes.TryGetValue(c.UniqueId, out List<string> uids) || uids.Count == 0) return null;
            var set = new HashSet<ElementId>();
            foreach (string uid in uids)
            {
                Element e = _doc.GetElement(uid);
                if (e == null || !_edges.ContainsKey(e.Id)) return null;
                set.Add(e.Id);
            }
            return set;
        }

        /// <summary>
        /// Rotas equilibradas: refaz o caminho de cada circuito penalizando os trechos já ocupados por outros circuitos,
        /// para reduzir o agrupamento. Devolve circuito → trechos.
        /// </summary>
        public Dictionary<ElementId, HashSet<ElementId>> Balanced(IList<ElectricalSystem> circuits, double weight, int passes = 3)
        {
            var result = new Dictionary<ElementId, HashSet<ElementId>>();
            var load = new Dictionary<ElementId, int>();
            void Count(HashSet<ElementId> segs, int delta)
            {
                foreach (ElementId s in segs) load[s] = (load.TryGetValue(s, out int v) ? v : 0) + delta;
            }

            for (int pass = 0; pass < passes; pass++)
            {
                foreach (ElectricalSystem c in circuits)
                {
                    if (result.TryGetValue(c.Id, out var old)) Count(old, -1);
                    Element panel = c.BaseEquipment;
                    if (panel == null) continue;
                    var (panelNode, _) = Entry(panel);
                    if (panelNode == null) continue;
                    Dictionary<ElementId, ElementId> tree = pass == 0
                        ? ShortestTree(panelNode)
                        : ShortestTree(panelNode, n => _lengthFt[n] > 0 ? 1.0 + weight * (load.TryGetValue(n, out int k) ? k : 0) : 1.0);
                    var segs = new HashSet<ElementId>();
                    foreach (Element device in c.Elements.Cast<Element>().Select(e => _doc.GetElement(e.Id)).Where(e => e != null))
                    {
                        var (node, _) = Entry(device);
                        if (node == null || !tree.ContainsKey(node)) continue;
                        for (ElementId n = node; n != null; n = tree[n]) segs.Add(n);
                    }
                    result[c.Id] = segs;
                    Count(segs, +1);
                }
            }
            return result;
        }

        /// <summary>Maior quantidade de circuitos num mesmo trecho reto, para uma distribuição de rotas.</summary>
        public int MaxGrouping(IEnumerable<HashSet<ElementId>> routes)
        {
            var load = new Dictionary<ElementId, int>();
            foreach (var r in routes)
                foreach (ElementId s in r.Where(IsCurve))
                    load[s] = (load.TryGetValue(s, out int v) ? v : 0) + 1;
            return load.Values.DefaultIfEmpty(0).Max();
        }

        public double LengthM(HashSet<ElementId> segs) => RevitHelpers.ToM(segs.Sum(s => _lengthFt.TryGetValue(s, out double l) ? l : 0));

        /// <summary>Eletroduto = infraestrutura fechada; eletrocalha pelo nome do tipo (perfurada, leito/escada ou lisa).</summary>
        private GroupingKind KindOf(Element e)
        {
            BuiltInCategory cat = e.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
            if (cat != BuiltInCategory.OST_CableTray && cat != BuiltInCategory.OST_CableTrayFitting) return GroupingKind.Closed;
            Element type = _doc.GetElement(e.GetTypeId());
            string name = ((type as ElementType)?.FamilyName + " " + type?.Name).ToLowerInvariant();
            if (name.Contains("perfur")) return GroupingKind.PerforatedTray;
            if (name.Contains("leito") || name.Contains("ladder") || name.Contains("escada")) return GroupingKind.Ladder;
            return GroupingKind.SmoothTray;
        }

        private bool IsCurve(ElementId id) => _lengthFt.TryGetValue(id, out double l) && l > 0;

        public static string Label(ElectricalSystem c) =>
            $"{(string.IsNullOrWhiteSpace(c.PanelName) ? "?" : c.PanelName)}-{c.CircuitNumber}";
    }
}
