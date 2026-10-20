using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Selection;
using RafaelElectrical.Services;

namespace RafaelElectrical.Commands
{
    /// <summary>Cor e helpers comuns do "Caminho do Circuito".</summary>
    internal static class CircuitPath
    {
        public const string Title = "Caminho do circuito";
        public static readonly Color Mark = new Color(230, 0, 126);

        public static readonly BuiltInCategory[] Infra =
        {
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        };

        public static bool ViewSupported(View v) =>
            v != null && !v.IsTemplate && (v is ViewPlan || v is View3D || v is ViewSection);

        public static bool IsMarked(OverrideGraphicSettings o)
        {
            Color c = o?.ProjectionLineColor;
            return c != null && c.IsValid && c.Red == Mark.Red && c.Green == Mark.Green && c.Blue == Mark.Blue;
        }
    }

    /// <summary>
    /// Destaca na vista ativa os eletrodutos/eletrocalhas por onde passa o circuito de um dispositivo
    /// (ou todos os circuitos de um quadro), usando o mesmo roteamento do Atualizar Quadros.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class HighlightCircuitPathCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            if (!CircuitPath.ViewSupported(view))
            {
                TaskDialog.Show(CircuitPath.Title, "Abra uma planta, um corte ou uma vista 3D.");
                return Result.Cancelled;
            }

            List<Element> picked = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            if (picked.Count == 0)
            {
                try
                {
                    picked.Add(doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element,
                        "Clique num dispositivo, luminária ou quadro para ver o caminho do circuito")));
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }

            var circuits = new Dictionary<ElementId, ElectricalSystem>();
            foreach (Element e in picked)
            {
                if (e is ElectricalSystem es) circuits[es.Id] = es;
                else if (CircuitDeviceFilter.IsPanel(e))
                {
                    foreach (ElectricalSystem c in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
                        if (c.BaseEquipment?.Id == e.Id) circuits[c.Id] = c;
                }
                else
                {
                    foreach (ElectricalSystem c in RevitHelpers.CircuitsOf(e)) circuits[c.Id] = c;
                }
            }
            if (circuits.Count == 0)
            {
                TaskDialog.Show(CircuitPath.Title, "O elemento escolhido não está em nenhum circuito.");
                return Result.Cancelled;
            }

            var routing = new RoutingService(doc);
            routing.RouteCircuits(circuits.Values.Where(c => c.BaseEquipment != null));

            var segments = new HashSet<ElementId>(routing.Routes.Where(r => r.Routed).SelectMany(r => r.Segments));
            ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id;

            int colored = 0;
            if (segments.Count > 0)
            {
                using var t = new Transaction(doc, "Destacar caminho do circuito");
                t.Start();
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(CircuitPath.Mark);
                ogs.SetCutLineColor(CircuitPath.Mark);
                ogs.SetProjectionLineWeight(6);
                if (solid != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solid);
                    ogs.SetSurfaceForegroundPatternColor(CircuitPath.Mark);
                }
                foreach (ElementId id in segments)
                {
                    try { view.SetElementOverrides(id, ogs); colored++; }
                    catch (Exception) { }
                }
                t.Commit();
            }

            var lines = routing.Routes.OrderBy(r => r.Circuit.PanelName).ThenBy(r => r.Circuit.CircuitNumber).Select(r =>
                r.Routed
                    ? $"{RoutingService.Label(r.Circuit)}: {r.LengthM:0.0} m, {r.DevicesReached}/{r.DevicesTotal} dispositivo(s), até {r.MaxGrouping} circuito(s) agrupados"
                    : $"{RoutingService.Label(r.Circuit)}: sem caminho (quadro ou dispositivos longe da infraestrutura)");

            TaskDialog.Show(CircuitPath.Title,
                (colored > 0
                    ? $"{colored} trecho(s) destacado(s) na vista \"{view.Name}\". Use Limpar Destaque para tirar as cores."
                    : "Nenhum trecho de infraestrutura encontrado para destacar.") +
                "\n\n" + RevitHelpers.Join(lines, 30));
            return Result.Succeeded;
        }
    }

    /// <summary>Remove da vista ativa apenas as cores aplicadas pelo Destacar Caminho.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ClearCircuitPathCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            View view = doc?.ActiveView;
            if (!CircuitPath.ViewSupported(view))
            {
                TaskDialog.Show(CircuitPath.Title, "Abra a vista em que o caminho foi destacado.");
                return Result.Cancelled;
            }

            List<ElementId> marked = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(CircuitPath.Infra))
                .WhereElementIsNotElementType()
                .ToElementIds()
                .Where(id => CircuitPath.IsMarked(view.GetElementOverrides(id)))
                .ToList();

            if (marked.Count > 0)
            {
                using var t = new Transaction(doc, "Limpar destaque do caminho");
                t.Start();
                foreach (ElementId id in marked) view.SetElementOverrides(id, new OverrideGraphicSettings());
                t.Commit();
            }

            TaskDialog.Show(CircuitPath.Title, marked.Count > 0
                ? $"Destaque removido de {marked.Count} trecho(s)."
                : "Não há trechos destacados nesta vista.");
            return Result.Succeeded;
        }
    }
}
