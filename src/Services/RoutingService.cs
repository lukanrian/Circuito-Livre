using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RafaelElectrical.Services
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

        public RoutingService(Document doc, double maxEntryDistanceM = 2.0)
        {
            _doc = doc;
            _maxEntryFt = RevitHelpers.M(maxEntryDistanceM);
            BuildGraph();
        }

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
        private Dictionary<ElementId, ElementId> ShortestTree(ElementId start)
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
                    double nd = d + (_lengthFt[node] + _lengthFt[next]) / 2;
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

                if (!treeCache.TryGetValue(panelNode, out var tree))
                    treeCache[panelNode] = tree = ShortestTree(panelNode);

                double dropsFt = panelDrop;
                foreach (Element device in devices)
                {
                    var (node, drop) = Entry(device);
                    if (node == null || !tree.ContainsKey(node)) continue;
                    route.DevicesReached++;
                    dropsFt += drop;
                    for (ElementId n = node; n != null; n = tree[n]) route.Segments.Add(n);
                }

                if (route.DevicesReached == 0) continue;
                route.LengthM = RevitHelpers.ToM(route.Segments.Sum(s => _lengthFt[s]) + dropsFt);
                foreach (ElementId s in route.Segments)
                {
                    if (!CircuitsBySegment.TryGetValue(s, out var set)) CircuitsBySegment[s] = set = new HashSet<ElementId>();
                    set.Add(c.Id);
                }
            }

            DimensioningSettings settings = DimensioningSettings.Load();
            foreach (CircuitRoute r in Routes.Where(x => x.Routed))
            {
                List<ElementId> curves = r.Segments.Where(IsCurve).ToList();
                r.MaxGrouping = curves.Select(s => CircuitsBySegment[s].Count).DefaultIfEmpty(1).Max();
                r.WorstFca = curves.Select(s => settings.GroupingFactor(_kind[s], CircuitsBySegment[s].Count)).DefaultIfEmpty(1.0).Min();
            }
        }

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
