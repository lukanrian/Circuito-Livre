using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;

namespace CircuitoLivre.Commands
{
    /// <summary>Confere cargas sem circuito e circuitos sem caminho completo na infraestrutura.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AuditElectricalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_LightingDevices,
                BuiltInCategory.OST_DataDevices,
                BuiltInCategory.OST_TelephoneDevices,
                BuiltInCategory.OST_FireAlarmDevices,
                BuiltInCategory.OST_CommunicationDevices,
                BuiltInCategory.OST_SecurityDevices,
            };
            List<FamilyInstance> devices = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType().OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.MEPModel?.ConnectorManager != null)
                .ToList();

            List<FamilyInstance> withoutCircuit = devices
                .Where(fi => RevitHelpers.CircuitsOf(fi).Count == 0 && CircuitDeviceFilter.GetFreeSystemTypes(fi).Count > 0)
                .ToList();

            List<ElectricalSystem> circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment != null && c.Elements.Size > 0)
                .ToList();
            var routing = new RoutingService(doc);
            routing.RouteCircuits(circuits);
            var incomplete = routing.Routes.Where(r => !r.Routed || r.DevicesReached < r.DevicesTotal).ToList();
            var unreachable = incomplete.SelectMany(r => r.Circuit.Elements.Cast<Element>()
                .Where(e => !r.ReachedDevices.Contains(e.Id)).Select(e => e.Id)).Distinct().ToList();

            var flagged = withoutCircuit.Select(fi => fi.Id).Concat(unreachable).Distinct().ToList();
            if (flagged.Count > 0) uidoc.Selection.SetElementIds(flagged);

            var lines = incomplete.Select(r =>
                $"{RoutingService.Label(r.Circuit)}: {r.DevicesReached}/{r.DevicesTotal} carga(s) alcançada(s)" +
                (r.PanelReached ? "" : " (quadro fora do alcance da infraestrutura)"));
            TaskDialog.Show("Auditoria elétrica",
                $"{devices.Count} dispositivo(s) com conectores analisados.\n" +
                $"{withoutCircuit.Count} sem circuito e com conector elétrico livre" +
                $".\n{unreachable.Count} carga(s) sem caminho completo." +
                (flagged.Count > 0 ? " Os pontos encontrados foram selecionados no modelo.\n" : "\n") +
                $"{incomplete.Count} de {circuits.Count} circuito(s) sem caminho completo.\n\n" +
                "O caminho usa a infraestrutura modelada e o limite de proximidade do roteamento.\n" +
                RevitHelpers.Join(lines, 25));
            return Result.Succeeded;
        }
    }
}
