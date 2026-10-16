using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Selection;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Centro de cargas: baricentro dos dispositivos ponderado pela potência (dos dispositivos selecionados
    /// ou de todos os circuitos de um quadro). Marca o ponto na planta ativa com uma cruz e um texto.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadCenterCommand : IExternalCommand
    {
        private class PoweredDeviceFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => PowerCircuitsOf(e).Any();
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            if (doc.ActiveView is not ViewPlan view)
            {
                TaskDialog.Show("Centro de cargas", "Abra uma planta para usar este comando.");
                return Result.Cancelled;
            }

            var dialog = new TaskDialog("Centro de cargas")
            {
                MainInstruction = "Calcular o centro de cargas de:",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Dispositivos que eu selecionar");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Todos os circuitos de um quadro");

            // dispositivo -> potência atribuída (VA)
            var weights = new Dictionary<ElementId, double>();
            string label;
            FamilyInstance panel = null;

            try
            {
                switch (dialog.Show())
                {
                    case TaskDialogResult.CommandLink1:
                    {
                        IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, new PoweredDeviceFilter(),
                            "Selecione os dispositivos (com circuito de força) e clique em Concluir");
                        foreach (Reference r in refs)
                        {
                            Element e = doc.GetElement(r);
                            foreach (ElectricalSystem c in PowerCircuitsOf(e))
                                Add(weights, e.Id, CircuitVA(c) / Math.Max(1, c.Elements.Size));
                        }
                        label = $"{refs.Count} dispositivo(s)";
                        break;
                    }
                    case TaskDialogResult.CommandLink2:
                    {
                        Reference r = uidoc.Selection.PickObject(ObjectType.Element, new PanelFilter(), "Selecione o quadro");
                        panel = (FamilyInstance)doc.GetElement(r);
                        foreach (ElectricalSystem c in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem))
                                     .Cast<ElectricalSystem>()
                                     .Where(c => c.SystemType == ElectricalSystemType.PowerCircuit && c.BaseEquipment?.Id == panel.Id))
                        {
                            int n = Math.Max(1, c.Elements.Size);
                            foreach (Element e in c.Elements) Add(weights, e.Id, CircuitVA(c) / n);
                        }
                        label = $"quadro {panel.Name}";
                        break;
                    }
                    default:
                        return Result.Cancelled;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            double total = 0, sx = 0, sy = 0;
            int counted = 0;
            foreach (var kv in weights)
            {
                XYZ p = PointOf(doc.GetElement(kv.Key));
                if (p == null) continue;
                double w = kv.Value > 0 ? kv.Value : 1e-6; // dispositivos sem carga quase não pesam
                sx += p.X * w;
                sy += p.Y * w;
                total += w;
                counted++;
            }

            if (counted == 0 || total <= 0)
            {
                TaskDialog.Show("Centro de cargas", "Nenhum dispositivo com posição encontrada.");
                return Result.Cancelled;
            }

            double z = view.GenLevel?.ProjectElevation ?? 0;
            var center = new XYZ(sx / total, sy / total, z);
            double totalVA = weights.Values.Sum();

            using (var t = new Transaction(doc, "Centro de cargas"))
            {
                t.Start();
                double arm = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Meters);
                doc.Create.NewDetailCurve(view, Line.CreateBound(center - XYZ.BasisX * arm, center + XYZ.BasisX * arm));
                doc.Create.NewDetailCurve(view, Line.CreateBound(center - XYZ.BasisY * arm, center + XYZ.BasisY * arm));
                ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                TextNote.Create(doc, view.Id, center + new XYZ(arm, arm, 0),
                    $"CENTRO DE CARGAS\n{label}\n{totalVA:N0} VA", textType);
                t.Commit();
            }

            string distance = "";
            if (panel != null && PointOf(panel) is XYZ pp)
            {
                double d = UnitUtils.ConvertFromInternalUnits(new XYZ(pp.X, pp.Y, 0).DistanceTo(new XYZ(center.X, center.Y, 0)), UnitTypeId.Meters);
                distance = $"\nDistância do quadro atual ao centro de cargas: {d:N2} m.";
            }

            TaskDialog.Show("Centro de cargas",
                $"Centro de cargas de {label} marcado na planta ({counted} dispositivo(s), {totalVA:N0} VA).{distance}");
            return Result.Succeeded;
        }

        private static void Add(Dictionary<ElementId, double> map, ElementId id, double va) =>
            map[id] = map.TryGetValue(id, out double v) ? v + va : va;

        private static double CircuitVA(ElectricalSystem c)
        {
            try { return UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes); }
            catch (Exception) { return 0; }
        }

        internal static IEnumerable<ElectricalSystem> PowerCircuitsOf(Element e)
        {
            if (e is not FamilyInstance fi || fi.MEPModel == null || CircuitDeviceFilter.IsPanel(fi))
                return Enumerable.Empty<ElectricalSystem>();
            try
            {
                return fi.MEPModel.GetElectricalSystems()
                    .Where(s => s.SystemType == ElectricalSystemType.PowerCircuit)
                    .ToList();
            }
            catch (Exception) { return Enumerable.Empty<ElectricalSystem>(); }
        }

        private static XYZ PointOf(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }
    }
}
