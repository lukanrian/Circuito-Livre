using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    // =====================================================================================
    //  Roteamento de circuitos (também usado por "Atualizar Quadros")
    // =====================================================================================

    /// <summary>
    /// Descobre o caminho de cada circuito pela infraestrutura e grava: nos eletrodutos/eletrocalhas os circuitos
    /// e cabos que passam (RE_Circuitos, RE_Nº de circuitos, RE_Cabos); nos circuitos o comprimento real e o
    /// agrupamento máximo (usados pelo Dimensionar).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RouteCircuitsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            HashSet<ElementId> panelIds = PanelScope.Ask(uidoc, "Rotear circuitos / Atualizar quadros");
            if (panelIds == null) return Result.Cancelled;

            List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment != null && (panelIds.Count == 0 || panelIds.Contains(c.BaseEquipment.Id)))
                .ToList();
            if (circuits.Count == 0)
            {
                TaskDialog.Show("Rotear circuitos", "Nenhum circuito encontrado.");
                return Result.Cancelled;
            }

            var routing = new RoutingService(doc);
            routing.RouteCircuits(circuits);

            using (var t = new Transaction(doc, "Rotear circuitos"))
            {
                t.Start();
                SharedParameters.EnsureRouting(doc);
                doc.Regenerate();
                InfraWriter.Write(doc, routing);
                t.Commit();
            }

            List<RoutingService.CircuitRoute> failed = routing.Routes.Where(r => !r.Routed).ToList();
            List<RoutingService.CircuitRoute> partial = routing.Routes.Where(r => r.Routed && r.DevicesReached < r.DevicesTotal).ToList();
            var lines = new List<string>();
            lines.AddRange(failed.Select(r => $"{RoutingService.Label(r.Circuit)}: sem caminho (quadro ou dispositivos longe da infraestrutura)."));
            lines.AddRange(partial.Select(r => $"{RoutingService.Label(r.Circuit)}: {r.DevicesReached}/{r.DevicesTotal} dispositivos alcançados."));

            TaskDialog.Show("Rotear circuitos",
                $"{routing.Routes.Count(r => r.Routed)} de {routing.Routes.Count} circuito(s) roteado(s); " +
                $"{routing.CircuitsBySegment.Count} trecho(s) de infraestrutura atualizado(s).\n" +
                "Rode o Dimensionar Instalação para usar o comprimento e o agrupamento reais." +
                (lines.Count > 0 ? "\n\n" + RevitHelpers.Join(lines, 30) : ""));
            return Result.Succeeded;
        }
    }

    /// <summary>Escolha de abrangência: quadros selecionados ou todos (conjunto vazio = todos).</summary>
    internal static class PanelScope
    {
        public static HashSet<ElementId> Ask(UIDocument uidoc, string title)
        {
            Document doc = uidoc.Document;
            var dialog = new TaskDialog(title) { MainInstruction = "Quais quadros?", CommonButtons = TaskDialogCommonButtons.Cancel };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Quadros que eu selecionar");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Todos os quadros do projeto");
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink2:
                    return new HashSet<ElementId>();
                case TaskDialogResult.CommandLink1:
                    try
                    {
                        List<ElementId> ids = uidoc.Selection.GetElementIds().Where(id => CircuitDeviceFilter.IsPanel(doc.GetElement(id))).ToList();
                        if (ids.Count == 0)
                            ids = uidoc.Selection.PickObjects(ObjectType.Element, new PanelFilter(), "Selecione os quadros e clique em Concluir")
                                .Select(r => r.ElementId).ToList();
                        return ids.Count > 0 ? new HashSet<ElementId>(ids) : null;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
                default:
                    return null;
            }
        }
    }

    /// <summary>Grava o resultado do roteamento nos parâmetros RE_.</summary>
    internal static class InfraWriter
    {
        public static void Write(Document doc, RoutingService routing)
        {
            double omit = DimensioningSettings.Load().OmitSectionInTags;
            string omitText = omit > 0 ? $" #{Nbr5410.FormatSection(omit)} mm²" : null;
            Dictionary<ElementId, ElectricalSystem> circuits = routing.Routes.ToDictionary(r => r.Circuit.Id, r => r.Circuit);

            foreach (var kv in routing.CircuitsBySegment)
            {
                Element seg = doc.GetElement(kv.Key);
                List<ElectricalSystem> list = kv.Value.Select(id => circuits[id])
                    .OrderBy(c => c.PanelName).ThenBy(c => c.CircuitNumber).ToList();
                SharedParameters.SetText(seg, SharedParameters.RouteCircuits, string.Join(", ", list.Select(RoutingService.Label)));
                SharedParameters.SetNumber(seg, SharedParameters.RouteCount, list.Count);
                SharedParameters.SetText(seg, SharedParameters.RouteCables, string.Join(" | ", list.Select(c =>
                {
                    string wiring = SharedParameters.GetText(c, SharedParameters.Wiring);
                    if (string.IsNullOrEmpty(wiring)) return RoutingService.Label(c);
                    if (omitText != null && wiring.EndsWith(omitText) && wiring.IndexOf('#') == wiring.LastIndexOf('#'))
                        wiring = wiring.Substring(0, wiring.Length - omitText.Length);
                    return $"{RoutingService.Label(c)}: {wiring}";
                })));
            }

            foreach (RoutingService.CircuitRoute r in routing.Routes.Where(x => x.Routed))
            {
                SharedParameters.SetNumber(r.Circuit, SharedParameters.RealLength, Math.Round(r.LengthM, 2));
                SharedParameters.SetNumber(r.Circuit, SharedParameters.RealGrouping, r.MaxGrouping);
                SharedParameters.SetNumber(r.Circuit, SharedParameters.RealFca, Math.Round(r.WorstFca, 2));
            }
        }
    }

    // =====================================================================================
    //  Dimensionar infraestrutura
    // =====================================================================================

    /// <summary>
    /// Dimensiona o diâmetro dos eletrodutos pela taxa de ocupação da NBR 5410 (53% 1 cabo, 31% 2 cabos, 40% 3+),
    /// usando os circuitos roteados e as seções dimensionadas, e cria uma vista 3D colorida por situação.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SizeInfrastructureCommand : IExternalCommand
    {
        /// <summary>Diâmetro externo aproximado de cabos unipolares 450/750 V PVC (mm) por seção (mm²).</summary>
        private static readonly Dictionary<double, double> CableDiameterMm = new()
        {
            [0.5] = 2.2, [0.75] = 2.4, [1] = 2.6, [1.5] = 3.0, [2.5] = 3.6, [4] = 4.2, [6] = 4.8, [10] = 6.1,
            [16] = 7.1, [25] = 8.8, [35] = 10.0, [50] = 11.8, [70] = 13.6, [95] = 15.8, [120] = 17.4,
            [150] = 19.4, [185] = 21.6, [240] = 24.6, [300] = 27.4,
        };

        private enum Status { Ok, Changed, NoSize, NoCables }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>().Where(c => c.BaseEquipment != null).ToList();
            DimensioningSettings settings = DimensioningSettings.Load();
            var routing = new RoutingService(doc);
            routing.RouteCircuits(circuits);
            Dictionary<ElementId, ElectricalSystem> byId = circuits.ToDictionary(c => c.Id);

            List<double> inner = new List<double>(), nominal = new List<double>();
            try
            {
                foreach (var standard in ConduitSizeSettings.GetConduitSizeSettings(doc))
                {
                    foreach (ConduitSize size in standard.Value)
                    {
                        if (!size.UsedInSizing) continue;
                        inner.Add(size.InnerDiameter);
                        nominal.Add(size.NominalDiameter);
                    }
                    if (inner.Count > 0) break; // primeira norma com tamanhos
                }
            }
            catch (Exception) { }
            List<(double Inner, double Nominal)> sizes = inner.Zip(nominal, (i, n) => (i, n)).OrderBy(x => x.i).ToList();

            List<Conduit> conduits = new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<Conduit>().ToList();
            var status = new Dictionary<ElementId, Status>();
            int changed = 0, noSize = 0, empty = 0;

            using (var t = new Transaction(doc, "Dimensionar infraestrutura"))
            {
                t.Start();
                SharedParameters.EnsureRouting(doc);
                doc.Regenerate();
                InfraWriter.Write(doc, routing);

                foreach (Conduit conduit in conduits)
                {
                    if (!routing.CircuitsBySegment.TryGetValue(conduit.Id, out var ids) || ids.Count == 0)
                    {
                        status[conduit.Id] = Status.NoCables;
                        empty++;
                        continue;
                    }

                    var cables = new List<double>();
                    foreach (ElementId cid in ids)
                    {
                        ElectricalSystem c = byId[cid];
                        double phase = SharedParameters.GetNumber(c, SharedParameters.Section) ?? 2.5;
                        double pe = SharedParameters.GetNumber(c, SharedParameters.ProtectionSection) ?? phase;
                        int poles = Math.Max(1, c.PolesNumber);
                        bool power = c.SystemType == ElectricalSystemType.PowerCircuit;
                        if (!power) { cables.Add(settings.TelecomCableDiameterMm); continue; }
                        double neutral = SharedParameters.GetNumber(c, SharedParameters.NeutralSection) ?? phase;
                        int phases = poles >= 3 ? 3 : poles == 2 ? 2 : 1;
                        for (int i = 0; i < phases; i++) cables.Add(Diameter(phase));
                        if (poles != 2) cables.Add(Diameter(neutral));
                        cables.Add(Diameter(pe));
                    }

                    double area = cables.Sum(d => Math.PI * d * d / 4);
                    double limit = settings.OccupancyLimit(cables.Count);
                    double neededInnerMm = Math.Sqrt(area / limit * 4 / Math.PI);

                    Parameter diameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    double currentFt = diameter?.AsDouble() ?? 0;
                    var fit = sizes.FirstOrDefault(s => RevitHelpers.ToM(s.Inner) * 1000 >= neededInnerMm - 1e-6);

                    if (fit.Nominal <= 0)
                    {
                        status[conduit.Id] = Status.NoSize;
                        noSize++;
                    }
                    else if (Math.Abs(fit.Nominal - currentFt) < 1e-6)
                    {
                        status[conduit.Id] = Status.Ok;
                    }
                    else
                    {
                        try
                        {
                            if (diameter != null && !diameter.IsReadOnly && diameter.Set(fit.Nominal)) { status[conduit.Id] = Status.Changed; changed++; }
                            else status[conduit.Id] = Status.Ok;
                        }
                        catch (Exception) { status[conduit.Id] = Status.NoSize; noSize++; }
                    }

                    double innerMm = fit.Inner > 0 ? RevitHelpers.ToM(fit.Inner) * 1000 : RevitHelpers.ToM(currentFt) * 1000;
                    if (innerMm > 0)
                        SharedParameters.SetNumber(conduit, SharedParameters.RouteOccupancy, Math.Round(area / (Math.PI * innerMm * innerMm / 4) * 100, 1));
                }

                View3D view = Create3DView(doc);
                if (view != null) ColorView(doc, view, status);
                t.Commit();
            }

            TaskDialog.Show("Dimensionar infraestrutura",
                $"{conduits.Count} eletroduto(s) analisado(s): {changed} redimensionado(s), {noSize} sem diâmetro comercial suficiente, {empty} sem cabos.\n\n" +
                "Vista 3D \"RE - Dimensionar Infraestrutura\":\n• Ciano: atende\n• Verde: diâmetro alterado\n• Azul: nenhum diâmetro comercial atende\n• Vermelho: sem cabos" +
                (sizes.Count == 0 ? "\n\nAtenção: não foi possível ler a tabela de diâmetros de eletroduto do projeto (Configurações elétricas)." : ""));
            return Result.Succeeded;
        }

        private static double Diameter(double section) =>
            CableDiameterMm.TryGetValue(section, out double d) ? d : 3.6 * Math.Sqrt(section / 2.5);

        private static View3D Create3DView(Document doc)
        {
            const string name = "RE - Dimensionar Infraestrutura";
            ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null) return null;

            View3D existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null) return existing;

            View3D view = View3D.CreateIsometric(doc, type.Id);
            try { view.Name = name; } catch (Exception) { }
            return view;
        }

        private static void ColorView(Document doc, View view, Dictionary<ElementId, Status> status)
        {
            FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            var colors = new Dictionary<Status, Color>
            {
                [Status.Ok] = new Color(0, 190, 210),
                [Status.Changed] = new Color(40, 170, 60),
                [Status.NoSize] = new Color(30, 80, 220),
                [Status.NoCables] = new Color(220, 40, 40),
            };
            foreach (var kv in status)
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(colors[kv.Value]);
                if (solid != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solid.Id);
                    ogs.SetSurfaceForegroundPatternColor(colors[kv.Value]);
                }
                try { view.SetElementOverrides(kv.Key, ogs); } catch (Exception) { }
            }
        }
    }

    // =====================================================================================
    //  Construir infraestrutura (eletroduto / eletrocalha) entre pontos
    // =====================================================================================

    /// <summary>
    /// Constrói eletroduto ou eletrocalha ligando, em sequência, os elementos clicados (quadro primeiro):
    /// trecho horizontal em L na altura escolhida e descidas até cada dispositivo, com curvas e tês.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class BuildInfrastructureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.ActiveView is not ViewPlan plan || plan.GenLevel == null)
            {
                TaskDialog.Show("Construir infraestrutura", "Abra uma planta para usar este comando.");
                return Result.Cancelled;
            }

            var types = new List<CheckItem>();
            types.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ElementType>()
                .Select(t => new CheckItem($"Eletroduto: {t.Name}", t)));
            types.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<ElementType>()
                .Select(t => new CheckItem($"Eletrocalha: {t.Name}", t)));
            if (types.Count == 0)
            {
                TaskDialog.Show("Construir infraestrutura", "Nenhum tipo de eletroduto ou eletrocalha no projeto.");
                return Result.Cancelled;
            }

            var window = new ChecklistWindow("Construir infraestrutura",
                    "Depois de OK, clique nos elementos na ordem do caminho (comece pelo quadro). ESC termina.",
                    new List<CheckItem>(), "OK")
                .AllowEmpty()
                .WithCombo("Tipo:", types)
                .WithText("Altura do trecho horizontal em relação ao nível (m):", "2,80");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            if (!double.TryParse(window.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double heightM))
            {
                TaskDialog.Show("Construir infraestrutura", "Altura inválida.");
                return Result.Cancelled;
            }
            var type = (ElementType)window.ComboSelection.Tag;
            bool tray = type is CableTrayType;

            var points = new List<XYZ>();
            while (true)
            {
                try
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, $"Clique no {points.Count + 1}º elemento do caminho (ESC termina)");
                    XYZ p = RevitHelpers.PointOf(doc.GetElement(r));
                    if (p != null) points.Add(p);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
            }
            if (points.Count < 2) return Result.Cancelled;

            Level level = plan.GenLevel;
            double h = level.ProjectElevation + RevitHelpers.M(heightM);
            int created = 0, fittingFail = 0;

            using (var t = new Transaction(doc, "Construir infraestrutura"))
            {
                t.Start();
                MEPCurve previousHorizontal = null;
                MEPCurve firstRiser = Segment(doc, tray, type.Id, level.Id, points[0], new XYZ(points[0].X, points[0].Y, h), ref created);

                for (int i = 1; i < points.Count; i++)
                {
                    XYZ a = new XYZ(points[i - 1].X, points[i - 1].Y, h);
                    XYZ b = new XYZ(points[i].X, points[i].Y, h);
                    XYZ corner = new XYZ(b.X, a.Y, h);

                    MEPCurve s1 = Segment(doc, tray, type.Id, level.Id, a, corner, ref created);
                    MEPCurve s2 = Segment(doc, tray, type.Id, level.Id, corner, b, ref created);
                    MEPCurve drop = Segment(doc, tray, type.Id, level.Id, b, points[i], ref created);

                    MEPCurve first = s1 ?? s2;
                    MEPCurve last = s2 ?? s1;
                    if (first == null) continue;

                    // início do trecho: curva com a subida do quadro ou tê com a descida anterior
                    if (i == 1 && firstRiser != null) fittingFail += Connect(doc, firstRiser, first, a) ? 0 : 1;
                    if (s1 != null && s2 != null) fittingFail += Connect(doc, s1, s2, corner) ? 0 : 1;
                    if (previousHorizontal != null) fittingFail += Connect(doc, previousHorizontal, first, a) ? 0 : 1;

                    // fim do trecho: descida até o dispositivo
                    if (drop != null) fittingFail += Connect(doc, last, drop, b) ? 0 : 1;
                    previousHorizontal = last;
                }
                t.Commit();
            }

            TaskDialog.Show("Construir infraestrutura",
                $"{created} trecho(s) criado(s)." +
                (fittingFail > 0 ? $"\n{fittingFail} conexão(ões) não puderam ser feitas automaticamente (verifique as conexões do tipo em Preferências de roteamento)." : ""));
            return Result.Succeeded;
        }

        private static MEPCurve Segment(Document doc, bool tray, ElementId typeId, ElementId levelId, XYZ a, XYZ b, ref int created)
        {
            if (a.DistanceTo(b) < RevitHelpers.M(0.02)) return null;
            MEPCurve curve = tray
                ? CableTray.Create(doc, typeId, a, b, levelId)
                : Conduit.Create(doc, typeId, a, b, levelId);
            created++;
            return curve;
        }

        /// <summary>Liga dois trechos no ponto comum (curva; se já houver conexão no ponto, tenta tê).</summary>
        private static bool Connect(Document doc, MEPCurve a, MEPCurve b, XYZ at)
        {
            Connector ca = Nearest(a, at), cb = Nearest(b, at);
            if (ca == null || cb == null) return false;
            try
            {
                if (ca.IsConnected)
                {
                    Connector other = ca.AllRefs.Cast<Connector>().FirstOrDefault(r => r.Owner.Id != a.Id);
                    if (other?.Owner is FamilyInstance elbow)
                    {
                        // havia uma curva: troca por tê (trecho que continua + descida + novo trecho)
                        Connector far = elbow.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                            .SelectMany(c => c.AllRefs.Cast<Connector>()).FirstOrDefault(r => r.Owner.Id != a.Id && r.Owner.Id != elbow.Id);
                        if (far?.Owner is MEPCurve farCurve)
                        {
                            foreach (int order in new[] { 0, 1, 2 })
                            {
                                using var sub = new SubTransaction(doc);
                                sub.Start();
                                try
                                {
                                    doc.Delete(elbow.Id);
                                    Connector c1 = Nearest(a, at), c2 = Nearest(b, at), c3 = Nearest(farCurve, at);
                                    if (order == 0) doc.Create.NewTeeFitting(c1, c2, c3);
                                    else if (order == 1) doc.Create.NewTeeFitting(c1, c3, c2);
                                    else doc.Create.NewTeeFitting(c2, c3, c1);
                                    sub.Commit();
                                    return true;
                                }
                                catch (Exception)
                                {
                                    sub.RollBack();
                                }
                            }
                        }
                    }
                    return false;
                }
                doc.Create.NewElbowFitting(ca, cb);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Connector Nearest(MEPCurve curve, XYZ p) =>
            curve.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End)
                .OrderBy(c => c.Origin.DistanceTo(p))
                .FirstOrDefault();
    }

    // =====================================================================================
    //  Suportes ao longo da infraestrutura
    // =====================================================================================

    /// <summary>Insere uma família de suporte a cada X metros ao longo dos eletrodutos/eletrocalhas selecionados.</summary>
    [Transaction(TransactionMode.Manual)]
    public class SupportsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<CheckItem> symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased &&
                            (s.Category?.BuiltInCategory == BuiltInCategory.OST_GenericModel ||
                             s.Category?.BuiltInCategory == BuiltInCategory.OST_SpecialityEquipment ||
                             s.Category?.BuiltInCategory == BuiltInCategory.OST_ConduitFitting ||
                             s.Category?.BuiltInCategory == BuiltInCategory.OST_CableTrayFitting))
                .OrderBy(s => s.FamilyName).Select(s => new CheckItem($"{s.FamilyName} : {s.Name}", s)).ToList();
            if (symbols.Count == 0)
            {
                TaskDialog.Show("Suportes", "Carregue no projeto uma família de suporte (Modelo genérico baseado em nível).");
                return Result.Cancelled;
            }

            List<MEPCurve> runs;
            try
            {
                runs = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<MEPCurve>()
                    .Where(c => c is Conduit || c is CableTray).ToList();
                if (runs.Count == 0)
                    runs = uidoc.Selection.PickObjects(ObjectType.Element, new RunFilter(), "Selecione eletrodutos/eletrocalhas e clique em Concluir")
                        .Select(r => doc.GetElement(r)).OfType<MEPCurve>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (runs.Count == 0) return Result.Cancelled;

            var window = new ChecklistWindow("Suportes", $"{runs.Count} trecho(s) selecionado(s).", new List<CheckItem>(), "Inserir")
                .AllowEmpty().WithCombo("Família:", symbols).WithText("Espaçamento máximo (m):", "1,50");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;
            if (!double.TryParse(window.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double spacingM) || spacingM <= 0.1)
                return Result.Cancelled;

            var symbol = (FamilySymbol)window.ComboSelection.Tag;
            int placed = 0;
            using (var t = new Transaction(doc, "Inserir suportes"))
            {
                t.Start();
                if (!symbol.IsActive) symbol.Activate();
                foreach (MEPCurve run in runs)
                {
                    if (run.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                    Level level = run.ReferenceLevel ?? doc.GetElement(run.LevelId) as Level;
                    if (level == null) continue;
                    double length = line.Length;
                    int n = Math.Max(1, (int)Math.Ceiling(length / RevitHelpers.M(spacingM)));
                    double step = length / n;
                    XYZ dir = line.Direction;
                    double angle = Math.Atan2(dir.Y, dir.X);
                    for (int i = 0; i <= n; i++)
                    {
                        XYZ p = line.GetEndPoint(0) + dir * (step * i);
                        if (i == 0 || i == n) p = p + dir * (i == 0 ? RevitHelpers.M(0.15) : -RevitHelpers.M(0.15));
                        FamilyInstance fi = doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, level.ProjectElevation), symbol, level, StructuralType.NonStructural);
                        fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)?.Set(p.Z - level.ProjectElevation);
                        if (Math.Abs(angle) > 1e-6)
                            ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(new XYZ(p.X, p.Y, 0), new XYZ(p.X, p.Y, 1)), angle);
                        placed++;
                    }
                }
                t.Commit();
            }

            TaskDialog.Show("Suportes", $"{placed} suporte(s) inserido(s).");
            return Result.Succeeded;
        }

        private class RunFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Conduit || e is CableTray;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
