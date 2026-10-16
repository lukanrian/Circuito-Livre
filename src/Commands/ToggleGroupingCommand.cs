using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Services;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Marca/desmarca circuitos para não entrarem no fator de agrupamento do dimensionamento
    /// (ex.: circuitos que seguem em eletroduto exclusivo).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleGroupingCommand : IExternalCommand
    {
        private class CircuitOrDeviceFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) =>
                e is ElectricalSystem es ? es.SystemType == ElectricalSystemType.PowerCircuit : CircuitsOf(e).Any();

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<Element> picked = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).ToList();
            if (!picked.Any(e => new CircuitOrDeviceFilter().AllowElement(e)))
            {
                try
                {
                    picked = uidoc.Selection.PickObjects(ObjectType.Element, new CircuitOrDeviceFilter(),
                            "Selecione dispositivos (ou circuitos) e clique em Concluir")
                        .Select(r => doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }

            var circuits = new Dictionary<ElementId, ElectricalSystem>();
            foreach (Element e in picked)
            {
                if (e is ElectricalSystem es && es.SystemType == ElectricalSystemType.PowerCircuit) circuits[es.Id] = es;
                foreach (ElectricalSystem c in CircuitsOf(e)) circuits[c.Id] = c;
            }
            if (circuits.Count == 0) return Result.Cancelled;

            var dialog = new TaskDialog("Desconsiderar agrupamento")
            {
                MainInstruction = $"{circuits.Count} circuito(s) selecionado(s).",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Desconsiderar agrupamento", "FCA = 1 no dimensionamento destes circuitos.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Voltar a considerar agrupamento");

            bool ignore;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: ignore = true; break;
                case TaskDialogResult.CommandLink2: ignore = false; break;
                default: return Result.Cancelled;
            }

            int count = 0;
            using (var t = new Transaction(doc, "Desconsiderar agrupamento"))
            {
                t.Start();
                try { SharedParameters.EnsureSizing(doc); }
                catch (Exception ex)
                {
                    t.RollBack();
                    TaskDialog.Show("Desconsiderar agrupamento", $"Não foi possível criar os parâmetros do plugin:\n{ex.Message}");
                    return Result.Failed;
                }
                doc.Regenerate();
                foreach (ElectricalSystem c in circuits.Values)
                    if (SharedParameters.SetFlag(c, SharedParameters.IgnoreGrouping, ignore)) count++;
                t.Commit();
            }

            TaskDialog.Show("Desconsiderar agrupamento",
                $"{count} circuito(s) {(ignore ? "não entrarão" : "voltarão a entrar")} no fator de agrupamento. Rode o Dimensionar para recalcular.");
            return Result.Succeeded;
        }

        private static IEnumerable<ElectricalSystem> CircuitsOf(Element e)
        {
            if (e is not FamilyInstance fi || fi.MEPModel == null) return Enumerable.Empty<ElectricalSystem>();
            try
            {
                return fi.MEPModel.GetElectricalSystems()
                    .Where(s => s.SystemType == ElectricalSystemType.PowerCircuit)
                    .ToList();
            }
            catch (Exception) { return Enumerable.Empty<ElectricalSystem>(); }
        }
    }
}
