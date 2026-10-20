using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Ajustar rota: procura caminhos alternativos para os circuitos dos quadros escolhidos, penalizando os trechos
    /// já ocupados, para reduzir o agrupamento (e a ocupação). Mostra antes/depois e, se aceito, guarda as rotas no
    /// projeto (o Atualizar Quadros passa a usá-las) e atualiza a fiação nos trechos.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class OptimizeRouteCommand : IExternalCommand
    {
        private const string Title = "Ajustar rota";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            HashSet<ElementId> panelIds = PanelScope.Ask(uidoc, Title);
            if (panelIds == null) return Result.Cancelled;

            var form = new FormWindow(Title, "Quanto maior o peso, mais o plugin aceita caminhos mais longos para fugir de trechos cheios.");
            var weight = form.Number("Peso do agrupamento (0,5 = leve; 3 = forte):", 1.0, 0.1, 20);
            var clear = form.Check("Apagar as rotas guardadas destes quadros e voltar ao caminho mais curto", false);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            List<ElectricalSystem> all = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment != null).ToList();
            List<ElectricalSystem> scope = all.Where(c => panelIds.Count == 0 || panelIds.Contains(c.BaseEquipment.Id)).ToList();
            if (scope.Count == 0) { TaskDialog.Show(Title, "Nenhum circuito encontrado."); return Result.Cancelled; }

            Dictionary<string, List<string>> stored = RoutingService.LoadStored(doc);
            if (clear.IsChecked == true)
            {
                foreach (ElectricalSystem c in scope) stored.Remove(c.UniqueId);
                Apply(doc, stored, all);
                TaskDialog.Show(Title, "Rotas guardadas apagadas: os circuitos voltam ao caminho mais curto.");
                return Result.Succeeded;
            }

            var routing = new RoutingService(doc) { UseStoredRoutes = false };
            routing.RouteCircuits(scope);
            Dictionary<ElementId, HashSet<ElementId>> before = routing.Routes.Where(r => r.Routed).ToDictionary(r => r.Circuit.Id, r => r.Segments);
            Dictionary<ElementId, HashSet<ElementId>> after = routing.Balanced(scope, FormWindow.NumberOf(weight, 1.0));
            if (before.Count == 0) { TaskDialog.Show(Title, "Nenhum circuito roteado pela infraestrutura (rode o Atualizar Quadros e confira a infraestrutura)."); return Result.Cancelled; }

            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            int maxBefore = routing.MaxGrouping(before.Values), maxAfter = routing.MaxGrouping(after.Values);
            double lenBefore = before.Values.Sum(v => routing.LengthM(v)), lenAfter = after.Values.Where(v => v.Count > 0).Sum(v => routing.LengthM(v));

            var rows = scope.Where(c => before.ContainsKey(c.Id)).OrderBy(c => c.PanelName).ThenBy(c => c.CircuitNumber).Select(c =>
            {
                HashSet<ElementId> b = before[c.Id], a = after.TryGetValue(c.Id, out var x) ? x : b;
                int gb = b.Max(s => before.Values.Count(r => r.Contains(s)));
                int ga = a.Count == 0 ? gb : a.Max(s => after.Values.Count(r => r.Contains(s)));
                return (IList<string>)new[]
                {
                    $"{c.PanelName}-{c.CircuitNumber}", c.LoadName ?? "",
                    routing.LengthM(b).ToString("0.0", br), routing.LengthM(a).ToString("0.0", br),
                    gb.ToString(), ga.ToString(), b.SetEquals(a) ? "" : "mudou",
                };
            }).ToList();

            string summary = $"Maior agrupamento num trecho: {maxBefore} → {maxAfter} circuitos.   " +
                             $"Comprimento total: {lenBefore.ToString("0", br)} m → {lenAfter.ToString("0", br)} m.";
            var window = new TableWindow(Title, summary,
                    new[] { "Circuito", "Descrição", "Antes (m)", "Depois (m)", "Agrup. antes", "Agrup. depois", "" }, rows, 900)
                .Action("Aplicar as novas rotas", true);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true || window.Chosen == null) return Result.Cancelled;

            foreach (ElectricalSystem c in scope)
            {
                if (!after.TryGetValue(c.Id, out var segs) || segs.Count == 0) continue;
                stored[c.UniqueId] = segs.Select(id => doc.GetElement(id)?.UniqueId).Where(u => u != null).ToList();
            }
            Apply(doc, stored, all);
            TaskDialog.Show(Title, "Rotas aplicadas e guardadas no projeto. Rode o Dimensionar Instalação para usar o novo agrupamento e comprimento.");
            return Result.Succeeded;
        }

        private static void Apply(Document doc, Dictionary<string, List<string>> stored, List<ElectricalSystem> all)
        {
            using var t = new Transaction(doc, Title);
            t.Start();
            RoutingService.SaveStored(doc, stored);
            SharedParameters.EnsureRouting(doc);
            doc.Regenerate();
            var routing = new RoutingService(doc) { StoredRoutes = stored };
            routing.RouteCircuits(all);
            InfraWriter.Write(doc, routing, clearUnused: true);
            t.Commit();
        }
    }
}
