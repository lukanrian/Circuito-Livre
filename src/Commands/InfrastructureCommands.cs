using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    // =====================================================================================
    //  Roteamento de circuitos (também usado por "Atualizar Quadros")
    // =====================================================================================

    /// <summary>
    /// Descobre o caminho de cada circuito pela infraestrutura e grava: nos eletrodutos/eletrocalhas os circuitos
    /// e cabos que passam (CL_Circuitos, CL_Nº de circuitos, CL_Cabos); nos circuitos o comprimento real e o
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

    /// <summary>Grava o resultado do roteamento nos parâmetros CL_.</summary>
    internal static class InfraWriter
    {
        public static void Write(Document doc, RoutingService routing, bool clearUnused = false)
        {
            DimensioningSettings cfg = DimensioningSettings.Load();
            double omit = cfg.OmitSectionInTags;
            string omitText = omit > 0 ? $" #{Nbr5410.FormatSection(omit)} mm²" : null;
            Dictionary<ElementId, ElectricalSystem> circuits = routing.Routes.ToDictionary(r => r.Circuit.Id, r => r.Circuit);

            // trechos que ficaram sem circuitos: limpa os dados antigos
            foreach (ElementId id in routing.Segments.Where(s => clearUnused && !routing.CircuitsBySegment.ContainsKey(s)))
            {
                Element seg = doc.GetElement(id);
                if (string.IsNullOrEmpty(SharedParameters.GetText(seg, SharedParameters.RouteCircuits))) continue;
                SharedParameters.SetText(seg, SharedParameters.RouteCircuits, "");
                SharedParameters.SetNumber(seg, SharedParameters.RouteCount, 0);
                SharedParameters.SetText(seg, SharedParameters.RouteCables, "");
            }

            foreach (var kv in routing.CircuitsBySegment)
            {
                Element seg = doc.GetElement(kv.Key);
                List<ElectricalSystem> list = kv.Value.Select(id => circuits[id])
                    .OrderBy(c => c.PanelName).ThenBy(c => c.CircuitNumber).ToList();
                SharedParameters.SetText(seg, SharedParameters.RouteCircuits, string.Join(", ", list.Select(RoutingService.Label)));
                SharedParameters.SetNumber(seg, SharedParameters.RouteCount, list.Count);
                SharedParameters.SetText(seg, SharedParameters.RouteCables, string.Join(" | ", list.Select(c =>
                {
                    if (c.SystemType != ElectricalSystemType.PowerCircuit)
                    {
                        string cable = c.SystemType switch
                        {
                            ElectricalSystemType.Data => cfg.CableData,
                            ElectricalSystemType.Telephone => cfg.CableTelephone,
                            ElectricalSystemType.Communication => cfg.CableCommunication,
                            ElectricalSystemType.Security => cfg.CableSecurity,
                            _ => null
                        };
                        if (cable != null) return cfg.TelecomVerboseNomenclature
                            ? $"{RoutingService.Label(c)}: {cfg.TelecomName(cable)}" : cfg.TelecomName(cable);
                    }
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
                        if (!power)
                        {
                            string cable = c.SystemType switch
                            {
                                ElectricalSystemType.Data => settings.CableData,
                                ElectricalSystemType.Telephone => settings.CableTelephone,
                                ElectricalSystemType.Communication => settings.CableCommunication,
                                ElectricalSystemType.Security => settings.CableSecurity,
                                _ => null
                            };
                            cables.Add(settings.TelecomDiameter(cable));
                            continue;
                        }
                        double neutral = SharedParameters.GetNumber(c, SharedParameters.NeutralSection) ?? phase;
                        int phases = poles >= 3 ? 3 : poles == 2 ? 2 : 1;
                        for (int i = 0; i < phases; i++) cables.Add(settings.CableDiameter(phase));
                        if (poles != 2) cables.Add(settings.CableDiameter(neutral));
                        cables.Add(settings.CableDiameter(pe));
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
    /// Constrói eletroduto ou eletrocalha entre componentes selecionados:
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

            var settings = DimensioningSettings.Load();
            var window = new ConduitBuilderWindow(types, settings, settings.InfraHeight(plan.GenLevel.Name));
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;
            settings.Save();

            ElementType ceilingType = window.CeilingType, wallType = window.WallType;
            var devices = new List<ElementId>();
            while (true)
            {
                try
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, $"Clique no {devices.Count + 1}º componente do caminho (ESC termina)");
                    AddPoint(doc.GetElement(r), devices);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
            }
            if (devices.Count < 2)
            {
                TaskDialog.Show("Construir infraestrutura", "São necessários pelo menos dois componentes com posição válida para criar uma rota.");
                return Result.Cancelled;
            }
            TaskDialog.Show("Construir infraestrutura", RunBuild(doc, plan.GenLevel, window.HeightM, ceilingType, wallType, settings, devices));
            return Result.Succeeded;
        }

        /// <summary>Aplica o mesmo lançamento sequencial na janela e no painel lateral.</summary>
        public static string RunBuild(Document doc, Level level, double heightM, ElementType ceilingType, ElementType wallType,
            DimensioningSettings settings, List<ElementId> devices)
        {
            if (settings.ConduitThroughFloor) heightM = settings.ConduitFloorOffsetM;
            if (!double.IsFinite(heightM) || !double.IsFinite(settings.ConduitBuilderDiameterMm) || settings.ConduitBuilderDiameterMm <= 0)
                return "Altura ou diâmetro inválido. Nenhum trecho criado.";
            double diameter = RevitHelpers.M(settings.ConduitBuilderDiameterMm / 1000);
            double h = level.ProjectElevation + RevitHelpers.M(heightM);
            double minimumLength = Math.Max(doc.Application.ShortCurveTolerance * 2, RevitHelpers.M(.02));
            double initialStub = Math.Max(RevitHelpers.M(.1), diameter * 4);
            int created = 0, plugged = 0, failed = 0, rejected = 0, withoutConnector = 0;
            ElementId previousTail = null;
            using var build = new TransactionGroup(doc, "Construir infraestrutura");
            build.Start();
            var failures = new List<string>();
            for (int i = 1; i < devices.Count; i++)
            {
                ElementId ownerA = devices[i - 1], ownerB = devices[i];
                var optionsA = EndpointOptions(doc.GetElement(ownerA), wallType is CableTrayType);
                var optionsB = EndpointOptions(doc.GetElement(ownerB), wallType is CableTrayType);
                if (settings.ConduitThroughFloor && wallType is ConduitType)
                {
                    optionsA.RemoveAll(p => p.Id < 0);
                    optionsB.RemoveAll(p => p.Id < 0);
                    if (optionsA.Count == 0 || optionsB.Count == 0)
                    {
                        Element missing = doc.GetElement(optionsA.Count == 0 ? ownerA : ownerB);
                        string failure = $"Pelo piso: {missing.Name} (ID {missing.Id.Value}) não possui conector de eletroduto livre e compatível. Confira a família e as ligações existentes.";
                        if (failures.Count < 3 && !failures.Contains(failure)) failures.Add(failure);
                        failed++;
                        previousTail = null;
                        continue;
                    }
                }
                Transform frame = WallFrame(doc.GetElement(ownerA), doc.GetElement(ownerB));
                Transform inverse = frame.Inverse;
                var pairs = (from a in optionsA from b in optionsB
                    orderby settings.ConduitThroughFloor && wallType is ConduitType
                        ? ConduitRouting.FloorOutletPriority((a.Direction.X, a.Direction.Y, a.Direction.Z)) +
                          ConduitRouting.FloorOutletPriority((b.Direction.X, b.Direction.Y, b.Direction.Z)) : 0,
                        a.Point.DistanceTo(b.Point) select (a, b)).ToList();
                bool startOnWall = settings.ConduitUsesHorizontalPlane && WallPlacement.IsWallMounted(doc, doc.GetElement(ownerA));
                bool endOnWall = settings.ConduitUsesHorizontalPlane && WallPlacement.IsWallMounted(doc, doc.GetElement(ownerB));
                bool success = false;
                foreach (var (a, b) in pairs)
                {
                    XYZ localA = inverse.OfPoint(a.Point), localB = inverse.OfPoint(b.Point);
                    XYZ directionA = inverse.OfVector(a.Direction), directionB = inverse.OfVector(b.Direction);
                    foreach (var candidate in ConduitRouting.CandidateRoutes((localA.X, localA.Y, localA.Z),
                        (directionA.X, directionA.Y, directionA.Z), (localB.X, localB.Y, localB.Z),
                        (directionB.X, directionB.Y, directionB.Z), settings.ConduitPlanAngle, h,
                        settings.ConduitOrientation, settings.ConduitWallAngle, settings.ConduitUsesHorizontalPlane, initialStub, minimumLength, startOnWall, endOnWall))
                    {
                        var way = candidate.Select(p => frame.OfPoint(new XYZ(p.X, p.Y, p.Z)))
                            .Select(p => (p.X, p.Y, p.Z)).ToList();
                        if (!TryBuildLeg(doc, level, ceilingType, wallType, diameter, h, settings, way,
                            ownerA, a.Id, ownerB, b.Id, a.Id < 0 ? previousTail : null, out var segments, out string failure))
                        {
                            rejected++;
                            if (failures.Count < 3 && !failures.Contains(failure)) failures.Add(failure);
                            continue;
                        }
                        created += segments.Count;
                        plugged += (a.Id >= 0 ? 1 : 0) + (b.Id >= 0 ? 1 : 0);
                        withoutConnector += (a.Id < 0 ? 1 : 0) + (b.Id < 0 ? 1 : 0);
                        previousTail = b.Id < 0 ? segments[^1].Id : null;
                        success = true;
                        break;
                    }
                    if (success) break;
                }
                if (!success) { failed++; previousTail = null; }
            }
            if (created > 0)
            {
                bool dirty = DimensioningSettings.ProjectDirty;
                try
                {
                    using var t = new Transaction(doc, "Guardar configurações de infraestrutura");
                    t.Start();
                    ProjectStorage.FlushSettings(doc);
                    if (t.Commit() != TransactionStatus.Committed || build.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("O Revit não confirmou a construção da infraestrutura.");
                }
                catch { DimensioningSettings.ProjectDirty = dirty; throw; }
            }
            else build.RollBack();
            return $"{created} trecho(s) criado(s); {plugged} ligação(ões) a conectores." +
                (failed > 0 ? $"\n{failed} percurso(s) entre componentes não criado(s): confira conectores livres, tipos e espaço para curvas." : "") +
                (rejected > 0 ? $"\n{rejected} tentativa(s) desfeita(s) integralmente antes de tentar outra solução." : "") +
                (withoutConnector > 0 ? $"\n{withoutConnector} extremidade(s) sem conector de infraestrutura; confira a continuidade." : "") +
                (failures.Count > 0 && failed > 0 ? "\nMotivo das falhas:\n" + string.Join("\n", failures) : "");
        }

        private static bool TryBuildLeg(Document doc, Level level, ElementType ceilingType, ElementType wallType,
            double diameter, double h, DimensioningSettings settings, List<(double X, double Y, double Z)> way,
            ElementId ownerA, int connectorA, ElementId ownerB, int connectorB, ElementId previousTail, out List<MEPCurve> segments, out string failure)
        {
            failure = null;
            string stepFailure = null, apiFailure = null;
            bool Step(string name, Func<bool> operation)
            {
                apiFailure = null;
                bool result = RollbackOnError.Try(doc, name, operation, out string reason);
                if (!result) stepFailure = name + ": " + (apiFailure ?? reason);
                return result;
            }
            segments = new List<MEPCurve>();
            var curves = segments;
            using var attempt = new TransactionGroup(doc, "Tentar percurso de infraestrutura");
            attempt.Start();
            bool ok = Step("Criar trechos de infraestrutura", () =>
            {
                for (int k = 1; k < way.Count; k++)
                {
                    bool ceiling = settings.ConduitUsesHorizontalPlane &&
                        ConduitRouting.AtCeiling(way[k - 1].Z, h) && ConduitRouting.AtCeiling(way[k].Z, h);
                    ElementType type = ceiling ? ceilingType : wallType;
                    var p = new XYZ(way[k - 1].X, way[k - 1].Y, way[k - 1].Z);
                    var q = new XYZ(way[k].X, way[k].Y, way[k].Z);
                    MEPCurve curve = Segment(doc, type is CableTrayType, type.Id, level.Id, p, q, diameter, settings.ConduitDiscipline);
                    if (curve == null) return false;
                    curves.Add(curve);
                }
                return curves.Count > 0;
            });
            for (int k = 1; ok && k < curves.Count; k++)
            {
                int index = k;
                var p = new XYZ(way[k].X, way[k].Y, way[k].Z);
                ok = Step("Criar curva " + index, () => Connect(doc, curves[index - 1], curves[index], p, message => apiFailure = message));
            }
            if (ok && previousTail != null)
                ok = Step("Continuar percurso", () => Connect(doc, doc.GetElement(previousTail) as MEPCurve,
                    curves[0], new XYZ(way[0].X, way[0].Y, way[0].Z)));
            if (ok && connectorA >= 0)
                ok = Step("Ligar componente inicial", () => Plug(curves[0], FindConnector(doc, ownerA, connectorA), message => apiFailure = message));
            if (ok && connectorB >= 0)
                ok = Step("Ligar componente final", () => Plug(curves[^1], FindConnector(doc, ownerB, connectorB), message => apiFailure = message));
            if (ok && attempt.Assimilate() == TransactionStatus.Committed) return true;
            if (attempt.GetStatus() == TransactionStatus.Started) attempt.RollBack();
            failure = stepFailure ?? "O Revit não confirmou o percurso.";
            curves.Clear();
            return false;
        }

        private static Transform WallFrame(Element a, Element b)
        {
            if ((a as FamilyInstance)?.Host is not Wall wallA || (b as FamilyInstance)?.Host is not Wall wallB ||
                wallA.Id != wallB.Id || wallA.Location is not LocationCurve location || location.Curve is not Line line)
                return Transform.Identity;
            XYZ direction = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
            Transform frame = Transform.Identity;
            frame.BasisX = direction;
            frame.BasisY = XYZ.BasisZ.CrossProduct(direction);
            return frame;
        }

        private static List<(int Id, XYZ Point, XYZ Direction)> EndpointOptions(Element element, bool tray)
        {
            var connectors = InfrastructureConnectors(element);
            var options = connectors.Where(c => !c.IsConnected &&
                c.Shape == (tray ? ConnectorProfileType.Rectangular : ConnectorProfileType.Round))
                .OrderBy(c => c.Id).Select(c => (c.Id, c.Origin, c.CoordinateSystem.BasisZ)).ToList();
            if (connectors.Count == 0 && RevitHelpers.PointOf(element) is XYZ point)
                options.Add((-1, point, XYZ.Zero));
            return options;
        }

        private static List<Connector> InfrastructureConnectors(Element element)
        {
            var manager = (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            return manager == null ? new List<Connector>() : manager.Connectors.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainCableTrayConduit && c.ConnectorType == ConnectorType.End).ToList();
        }

        // Busca por identidade a cada tentativa: o rollback pode invalidar objetos Connector em memória.
        private static Connector FindConnector(Document doc, ElementId owner, int id) =>
            InfrastructureConnectors(doc.GetElement(owner)).FirstOrDefault(c => c.Id == id);

        internal static void AddPoint(Element element, List<ElementId> devices)
        {
            if (element is not FamilyInstance || devices.Contains(element.Id)) return;
            if (RevitHelpers.PointOf(element) != null || InfrastructureConnectors(element).Count > 0) devices.Add(element.Id);
        }

        private static MEPCurve Segment(Document doc, bool tray, ElementId typeId, ElementId levelId, XYZ a, XYZ b,
            double diameterInternal, string discipline)
        {
            if (a.DistanceTo(b) < RevitHelpers.M(0.02)) return null;
            MEPCurve curve = tray
                ? CableTray.Create(doc, typeId, a, b, levelId)
                : Conduit.Create(doc, typeId, a, b, levelId);
            if (!tray && diameterInternal > 0)
            {
                Parameter parameter = curve.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                if (parameter == null) throw new InvalidOperationException("O tipo não informa o diâmetro do eletroduto.");
                if (Math.Abs(parameter.AsDouble() - diameterInternal) > 1e-6) parameter.Set(diameterInternal);
                if (Math.Abs(parameter.AsDouble() - diameterInternal) > 1e-6)
                    throw new InvalidOperationException("O tipo não aceita o diâmetro de eletroduto escolhido.");
            }
            if (!string.IsNullOrEmpty(discipline))
                try { curve.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(discipline); } catch { }
            return curve;
        }

        /// <summary>Liga dois trechos no ponto comum (curva; se já houver conexão no ponto, tenta tê).</summary>
        internal static bool Connect(Document doc, MEPCurve a, MEPCurve b, XYZ at, Action<string> onFailure = null)
        {
            Connector ca = Nearest(a, at), cb = Nearest(b, at);
            if (ca == null || cb == null) { onFailure?.Invoke("Conector da curva não encontrado."); return false; }
            try
            {
                if (ca.IsConnectedTo(cb)) return true;
                if (ca.IsConnected)
                {
                    Connector other = ca.AllRefs.Cast<Connector>().FirstOrDefault(r => r.Owner.Id != a.Id);
                    if (other?.Owner is FamilyInstance elbow)
                    {
                        // havia uma curva: troca por tê (trecho que continua + descida + novo trecho)
                        Connector far = elbow.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                            .SelectMany(c => c.AllRefs.Cast<Connector>()).FirstOrDefault(r => r.Owner.Id != a.Id && r.Owner.Id != elbow.Id);
                        if (far?.Owner.Id == b.Id) return true;
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
            catch (Exception ex) { onFailure?.Invoke(ex.Message); return false; }
        }

        /// <summary>Liga a ponta do trecho ao conector da família, se coincidirem em posição e direção.</summary>
        private static bool Plug(MEPCurve curve, Connector plug, Action<string> onFailure = null)
        {
            bool Fail(string reason) { onFailure?.Invoke(reason); return false; }
            if (curve == null || plug == null) return Fail("Conector do componente não encontrado.");
            try
            {
                Connector end = Nearest(curve, plug.Origin);
                if (end == null || end.IsConnected || plug.IsConnected) return Fail("Conector ausente ou já ocupado.");
                if (end.Origin.DistanceTo(plug.Origin) > RevitHelpers.M(0.01)) return Fail("A curva deslocou a ponta para fora do conector do componente.");
                XYZ outgoing = -end.CoordinateSystem.BasisZ, direction = plug.CoordinateSystem.BasisZ;
                if (!ConduitRouting.SameDirection((outgoing.X, outgoing.Y, outgoing.Z), (direction.X, direction.Y, direction.Z))) return Fail("Sentido da ponta incompatível com o conector do componente.");
                end.ConnectTo(plug);
                return true;
            }
            catch (Exception ex) { onFailure?.Invoke(ex.Message); return false; }
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
