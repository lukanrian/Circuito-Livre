using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Reorganiza as posições (slots) dos circuitos de um quadro de força ou de telecom pela tabela de quadro.
    /// Ordens possíveis: atual, descrição, carga, ordem de clique nos dispositivos, ordem dos ambientes ou posição na planta.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RenumberCircuitsCommand : IExternalCommand
    {
        private const double RowToleranceFt = 3.28084; // 1 m

        /// <summary>Ordem já marcada ao abrir a janela (botões "por dispositivos", "por espaços").</summary>
        protected virtual RenumberMode? Preset => null;

        private class AreaRef
        {
            public string Name;
            public Func<XYZ, bool> Contains;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            try
            {
                // 1. Quadro
                FamilyInstance panel = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilyInstance>()
                    .FirstOrDefault(CircuitDeviceFilter.IsPanel);
                if (panel == null)
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, new PanelFilter(), "Selecione o quadro/painel a renumerar");
                    panel = (FamilyInstance)doc.GetElement(r);
                }

                List<ElectricalSystem> circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(c => c.BaseEquipment?.Id == panel.Id)
                    .ToList();

                if (circuits.Count == 0)
                {
                    TaskDialog.Show("Renumerar circuitos", $"O quadro \"{panel.Name}\" não tem circuitos.");
                    return Result.Cancelled;
                }

                // 2. Opções
                var window = new RenumberWindow(panel.Name, circuits.Count, Preset);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                if (window.ShowDialog() != true) return Result.Cancelled;

                Dictionary<ElementId, XYZ> centers = circuits.ToDictionary(c => c.Id, c => Centroid(c));
                List<ElectricalSystem> ordered = Order(uidoc, circuits, centers, window.Mode, window.Geo);
                if (ordered == null) return Result.Cancelled;

                // 3. Movimentação
                return Apply(doc, panel, ordered, window.StartSlot);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
        }

        // ---------------- ordenação ----------------

        private static List<ElectricalSystem> Order(UIDocument uidoc, List<ElectricalSystem> circuits,
            Dictionary<ElementId, XYZ> centers, RenumberMode mode, GeoOrder geo)
        {
            IOrderedEnumerable<ElectricalSystem> ThenGeo(IOrderedEnumerable<ElectricalSystem> q) =>
                geo == GeoOrder.RowsLeftToRight
                    ? q.ThenBy(c => -Math.Round(centers[c.Id].Y / RowToleranceFt)).ThenBy(c => centers[c.Id].X)
                    : q.ThenBy(c => Math.Round(centers[c.Id].X / RowToleranceFt)).ThenBy(c => -centers[c.Id].Y);

            switch (mode)
            {
                case RenumberMode.ByName:
                    return circuits.OrderBy(c => c.LoadName ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.StartSlot).ToList();

                case RenumberMode.ByLoad:
                    return circuits.OrderByDescending(c => c.ApparentLoad).ThenBy(c => c.StartSlot).ToList();

                case RenumberMode.ByPosition:
                    return ThenGeo(circuits.OrderBy(c => 0)).ToList();

                case RenumberMode.ByClick:
                {
                    Dictionary<ElementId, int> rank = PickDevicesInOrder(uidoc, circuits);
                    return ThenGeo(circuits.OrderBy(c => rank.TryGetValue(c.Id, out int r) ? r : int.MaxValue)).ToList();
                }

                case RenumberMode.BySpaces:
                {
                    List<AreaRef> areas = PickAreasInOrder(uidoc);
                    if (areas == null) return null;
                    Document doc = uidoc.Document;
                    int AreaIndex(ElectricalSystem c)
                    {
                        int best = int.MaxValue;
                        foreach (Element e in c.Elements)
                        {
                            XYZ p = PointOf(doc.GetElement(e.Id));
                            if (p == null) continue;
                            int i = areas.FindIndex(a => a.Contains(p));
                            if (i >= 0 && i < best) best = i;
                        }
                        return best;
                    }
                    return ThenGeo(circuits.OrderBy(AreaIndex)).ToList();
                }

                default:
                    return circuits.OrderBy(c => c.StartSlot).ToList();
            }
        }

        private static Dictionary<ElementId, int> PickDevicesInOrder(UIDocument uidoc, List<ElectricalSystem> circuits)
        {
            var deviceToCircuits = new Dictionary<ElementId, List<ElementId>>();
            foreach (ElectricalSystem c in circuits)
                foreach (Element e in c.Elements)
                {
                    if (!deviceToCircuits.TryGetValue(e.Id, out var list)) deviceToCircuits[e.Id] = list = new List<ElementId>();
                    list.Add(c.Id);
                }

            var rank = new Dictionary<ElementId, int>();
            var filter = new IdSetFilter(new HashSet<ElementId>(deviceToCircuits.Keys));
            int k = 0;
            while (true)
            {
                Reference r;
                try
                {
                    r = uidoc.Selection.PickObject(ObjectType.Element, filter,
                        $"Clique no dispositivo do {rank.Count + 1}º circuito (ESC para terminar)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                foreach (ElementId cid in deviceToCircuits[r.ElementId])
                    if (!rank.ContainsKey(cid)) rank[cid] = k++;
            }
            return rank;
        }

        private static List<AreaRef> PickAreasInOrder(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var dialog = new TaskDialog("Renumerar circuitos")
            {
                MainInstruction = "Onde estão os ambientes?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Neste projeto", "Espaços (MEP) ou Ambientes modelados no projeto elétrico.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "No vínculo da arquitetura", "Ambientes do arquivo vinculado.");

            bool linked;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: linked = false; break;
                case TaskDialogResult.CommandLink2: linked = true; break;
                default: return null;
            }

            var areas = new List<AreaRef>();
            var used = new HashSet<string>();
            while (true)
            {
                Reference r;
                try
                {
                    r = linked
                        ? uidoc.Selection.PickObject(ObjectType.LinkedElement, new LinkedRoomFilter(doc), $"Clique no {areas.Count + 1}º ambiente (ESC para terminar)")
                        : uidoc.Selection.PickObject(ObjectType.Element, new SpatialPickFilter(), $"Clique no {areas.Count + 1}º ambiente/espaço (ESC para terminar)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                if (linked)
                {
                    var link = (RevitLinkInstance)doc.GetElement(r);
                    if (link.GetLinkDocument()?.GetElement(r.LinkedElementId) is not Room room) continue;
                    if (!used.Add($"{link.Id}:{room.Id}")) continue;
                    Transform inverse = link.GetTotalTransform().Inverse;
                    double z = (room.Level?.ProjectElevation ?? 0) + 1.0;
                    areas.Add(new AreaRef
                    {
                        Name = room.Name,
                        Contains = p => { XYZ q = inverse.OfPoint(p); return room.IsPointInRoom(new XYZ(q.X, q.Y, z)); },
                    });
                }
                else
                {
                    Element e = doc.GetElement(r);
                    if (!used.Add(e.Id.ToString())) continue;
                    if (e is Space space)
                    {
                        double z = (space.Level?.ProjectElevation ?? 0) + 1.0;
                        areas.Add(new AreaRef { Name = space.Name, Contains = p => space.IsPointInSpace(new XYZ(p.X, p.Y, z)) });
                    }
                    else if (e is Room room)
                    {
                        double z = (room.Level?.ProjectElevation ?? 0) + 1.0;
                        areas.Add(new AreaRef { Name = room.Name, Contains = p => room.IsPointInRoom(new XYZ(p.X, p.Y, z)) });
                    }
                }
            }
            return areas.Count > 0 ? areas : null;
        }

        private static XYZ Centroid(ElectricalSystem c)
        {
            var points = new List<XYZ>();
            foreach (Element e in c.Elements)
            {
                XYZ p = PointOf(e);
                if (p != null) points.Add(p);
            }
            if (points.Count == 0) return XYZ.Zero;
            return new XYZ(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));
        }

        private static XYZ PointOf(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            BoundingBoxXYZ bb = e?.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        // ---------------- movimentação na tabela de quadro ----------------

        private static Result Apply(Document doc, FamilyInstance panel, List<ElectricalSystem> ordered, int startSlot)
        {
            int moved = 0;
            var notMoved = new List<string>();

            using (var t = new Transaction(doc, "Renumerar circuitos"))
            {
                t.Start();

                PanelScheduleView schedule = GetOrCreateSchedule(doc, panel);
                if (schedule == null)
                {
                    t.RollBack();
                    TaskDialog.Show("Renumerar circuitos", "Não foi possível obter a tabela de quadro (Panel Schedule) deste quadro.");
                    return Result.Failed;
                }

                int maxSlot = GetMaxSlot(panel);
                var placed = new HashSet<ElementId>();

                foreach (ElectricalSystem circuit in ordered)
                {
                    doc.Regenerate();
                    int current = circuit.StartSlot;
                    Dictionary<int, ElementId> occupancy = ReadOccupancy(schedule, maxSlot);

                    bool done = false;
                    for (int slot = startSlot; slot <= maxSlot && !done; slot++)
                    {
                        if (occupancy.TryGetValue(slot, out ElementId occ) && placed.Contains(occ)) continue;
                        if (slot == current) { done = true; break; }
                        if (!TryGetCell(schedule, current, out int srcRow, out int srcCol)) break;
                        if (!TryGetCell(schedule, slot, out int dstRow, out int dstCol)) continue;

                        try
                        {
                            if (schedule.CanMoveSlotTo(srcRow, srcCol, dstRow, dstCol))
                            {
                                schedule.MoveSlotTo(srcRow, srcCol, dstRow, dstCol);
                                moved++;
                                done = true;
                            }
                        }
                        catch (Exception) { /* tenta a próxima posição */ }
                    }

                    if (!done) notMoved.Add($"{circuit.CircuitNumber} — {circuit.LoadName}");
                    placed.Add(circuit.Id);
                }

                t.Commit();
            }

            var report = new TaskDialog("Renumerar circuitos")
            {
                MainInstruction = $"{moved} circuito(s) reposicionado(s) em \"{panel.Name}\".",
            };
            if (notMoved.Count > 0)
            {
                report.MainContent = $"{notMoved.Count} circuito(s) ficaram na posição atual (travados, sem espaço ou polos incompatíveis).";
                report.ExpandedContent = string.Join("\n", notMoved);
            }
            report.Show();
            return Result.Succeeded;
        }

        internal static PanelScheduleView GetOrCreateSchedule(Document doc, FamilyInstance panel)
        {
            PanelScheduleView existing = new FilteredElementCollector(doc)
                .OfClass(typeof(PanelScheduleView))
                .Cast<PanelScheduleView>()
                .FirstOrDefault(v => !v.IsPanelScheduleTemplate() && v.GetPanel() == panel.Id);
            if (existing != null) return existing;

            try { return PanelScheduleView.CreateInstanceView(doc, panel.Id); }
            catch (Exception) { return null; }
        }

        internal static int GetMaxSlot(FamilyInstance panel)
        {
            Parameter p = panel.get_Parameter(BuiltInParameter.RBS_ELEC_MAX_POLE_BREAKERS);
            int value = p != null && p.HasValue ? p.AsInteger() : 0;
            return value > 0 ? value : 120;
        }

        internal static Dictionary<int, ElementId> ReadOccupancy(PanelScheduleView schedule, int maxSlot)
        {
            var map = new Dictionary<int, ElementId>();
            for (int slot = 1; slot <= maxSlot; slot++)
            {
                if (!TryGetCell(schedule, slot, out int row, out int col)) continue;
                try
                {
                    ElementId id = schedule.GetCircuitIdByCell(row, col);
                    if (id != null && id != ElementId.InvalidElementId) map[slot] = id;
                }
                catch (Exception) { }
            }
            return map;
        }

        internal static bool TryGetCell(PanelScheduleView schedule, int slot, out int row, out int col)
        {
            row = col = -1;
            try
            {
                schedule.GetCellsBySlotNumber(slot, out IList<int> rows, out IList<int> cols);
                if (rows == null || rows.Count == 0 || cols == null || cols.Count == 0) return false;
                row = rows[0];
                col = cols[0];
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
