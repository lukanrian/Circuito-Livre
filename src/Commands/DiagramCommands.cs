using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Diagrama unifilar de cada quadro numa vista de desenho, com disjuntores (curva e corrente), grupos de DR
    /// (setorização pelos circuitos que exigem DR), DPS e reservas. Conforme as Configurações, cria também o
    /// diagrama multifilar e o detalhe da proteção.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SingleLineDiagramCommand : IExternalCommand
    {
        private static readonly int[] DrRatings = { 25, 40, 63, 80, 100, 125 };

        private class Item
        {
            public ElectricalSystem Circuit;
            public bool Spare;
            public int Dr = -1; // índice do grupo de DR (-1 = sem DR)
            public double Breaker;
            public string Curve = "C";
            public string Wiring = "";
            public string CableType = "";
            public double Va;
            public int Poles = 1;
            public bool Neutral = true;
            public bool Ground = true;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            HashSet<ElementId> panelIds = PanelScope.Ask(uidoc, "Diagrama unifilar");
            if (panelIds == null) return Result.Cancelled;
            List<FamilyInstance> panels = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(p => CircuitDeviceFilter.IsPanel(p) && (panelIds.Count == 0 || panelIds.Contains(p.Id))).ToList();

            ViewFamilyType draftingType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
            if (draftingType == null || panels.Count == 0) return Result.Cancelled;

            DimensioningSettings cfg = DimensioningSettings.Load();
            if (!cfg.DiagramUnifilar && !cfg.DiagramMultifilar && !cfg.DiagramProtectionDetail)
            {
                TaskDialog.Show("Diagramas", "Marque ao menos um tipo de diagrama em Configurações → Documentação.");
                return Result.Cancelled;
            }
            View last = null;
            int count = 0;
            using (var t = new Transaction(doc, "Diagrama unifilar"))
            {
                t.Start();
                foreach (FamilyInstance panel in panels)
                {
                    List<Item> items = Items(doc, panel, cfg);
                    if (items.Count == 0) continue;
                    string panelName = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name;

                    if (cfg.DiagramUnifilar) last = SingleLine(doc, draftingType.Id, panelName, items, cfg);
                    if (cfg.DiagramMultifilar) Multifilar(doc, draftingType.Id, panelName, items, cfg);
                    if (cfg.DiagramProtectionDetail) ProtectionDetail(doc, draftingType.Id, panelName, items, cfg);
                    count++;
                }
                t.Commit();
            }

            if (last != null) uidoc.ActiveView = last;
            TaskDialog.Show("Diagramas", $"{count} quadro(s) diagramado(s)" + (cfg.DiagramUnifilar ? " em vistas de desenho \"RE - Unifilar ...\"" : "") +
                (cfg.DiagramMultifilar ? ", \"RE - Multifilar ...\"" : "") + (cfg.DiagramProtectionDetail ? " e \"RE - Proteção ...\"" : "") + ".");
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ dados

        private static List<Item> Items(Document doc, FamilyInstance panel, DimensioningSettings cfg)
        {
            List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment?.Id == panel.Id && c.SystemType == ElectricalSystemType.PowerCircuit)
                .OrderBy(c => c.StartSlot).ToList();

            var items = new List<Item>();
            foreach (ElectricalSystem c in circuits)
            {
                bool spare = false;
                try { spare = c.CircuitType != CircuitType.Circuit; } catch (Exception) { }
                if (spare && !cfg.DiagramShowSpares) continue;
                CircuitClassRule rule = cfg.ClassOf(c.LoadName);
                int poles = Math.Max(1, SafePoles(c));
                CircuitDocumentationRule documentation = cfg.DocumentationFor(c.LoadName, poles);
                items.Add(new Item
                {
                    Circuit = c,
                    Spare = spare,
                    Breaker = SharedParameters.GetNumber(c, SharedParameters.Breaker)
                              ?? UnitUtils.ConvertFromInternalUnits(c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0, UnitTypeId.Amperes),
                    Curve = documentation?.Curve ?? rule?.Curve ?? "C",
                    Wiring = SharedParameters.GetText(c, SharedParameters.Wiring) ?? "",
                    CableType = documentation?.CableType ?? "",
                    Va = UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes),
                    Poles = poles,
                    Neutral = documentation?.Neutral ?? rule?.Neutral ?? true,
                    Ground = documentation?.Ground ?? rule?.Ground ?? true,
                });
            }

            // setorização por DR: circuitos que exigem DR, em grupos de no máximo N
            List<Item> drItems = items.Where(i => !i.Spare && cfg.NeedsDr(i.Circuit.LoadName)).ToList();
            for (int k = 0; k < drItems.Count; k++) drItems[k].Dr = k / Math.Max(1, cfg.DrMaxCircuits);
            return items.OrderBy(i => i.Dr < 0 ? -1 : i.Dr).ThenBy(i => i.Circuit.StartSlot).ToList();
        }

        private static int SafePoles(ElectricalSystem c)
        {
            try { return c.PolesNumber; } catch (Exception) { return 1; }
        }

        private static int DrRating(IEnumerable<Item> group)
        {
            double sum = group.Sum(i => SharedParameters.GetNumber(i.Circuit, SharedParameters.Current) ?? i.Breaker * 0.8);
            int rating = DrRatings.FirstOrDefault(x => x >= sum);
            return rating > 0 ? rating : DrRatings.Last();
        }

        // ------------------------------------------------------------------ desenho

        private static ViewDrafting NewView(Document doc, ElementId type, string name, DimensioningSettings cfg)
        {
            ViewDrafting view = ViewDrafting.Create(doc, type);
            view.Scale = cfg.DiagramSize == "Pequeno" ? 100 : cfg.DiagramSize == "Médio" ? 75 : 50;
            try { view.Name = name; } catch (Exception) { try { view.Name = $"{name} {view.Id.Value}"; } catch (Exception) { } }
            return view;
        }

        private static View SingleLine(Document doc, ElementId type, string panelName, List<Item> items, DimensioningSettings cfg)
        {
            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            ViewDrafting view = NewView(doc, type, $"RE - Unifilar {panelName}", cfg);

            double s = RevitHelpers.M(0.6), busX = 0, lineEnd = RevitHelpers.M(3.6);
            double total = items.Where(i => !i.Spare).Sum(i => i.Va);
            Line(doc, view, new XYZ(busX, s, 0), new XYZ(busX, -s * items.Count, 0));
            Line(doc, view, new XYZ(busX, s, 0), new XYZ(busX, s * 2.5, 0));
            Breaker(doc, view, new XYZ(busX, s * 1.6, 0), vertical: true);
            TextNote.Create(doc, view.Id, new XYZ(busX + RevitHelpers.M(0.3), s * 3.2, 0),
                $"QUADRO {panelName} — carga instalada {total.ToString("N0", br)} VA", textType);

            if (cfg.DiagramUseDps)
            {
                double dx = -RevitHelpers.M(1.0), dy = s * 0.5;
                Line(doc, view, new XYZ(busX, dy, 0), new XYZ(dx, dy, 0));
                if (cfg.DiagramDpsBreaker) Breaker(doc, view, new XYZ(dx, dy - RevitHelpers.M(0.3), 0), vertical: true);
                Line(doc, view, new XYZ(dx, dy, 0), new XYZ(dx, dy - RevitHelpers.M(0.15), 0));
                Line(doc, view, new XYZ(dx, dy - RevitHelpers.M(0.45), 0), new XYZ(dx, dy - RevitHelpers.M(0.7), 0));
                Line(doc, view, new XYZ(dx - RevitHelpers.M(0.15), dy - RevitHelpers.M(0.7), 0), new XYZ(dx + RevitHelpers.M(0.15), dy - RevitHelpers.M(0.7), 0));
                Line(doc, view, new XYZ(dx - RevitHelpers.M(0.09), dy - RevitHelpers.M(0.76), 0), new XYZ(dx + RevitHelpers.M(0.09), dy - RevitHelpers.M(0.76), 0));
                string dps = cfg.DiagramDpsText != "DPS classe II 275 V 20 kA (F e N)" &&
                    !string.IsNullOrWhiteSpace(cfg.DiagramDpsText) ? cfg.DiagramDpsText :
                    $"DPS classe II {cfg.DiagramDpsVoltageV} V {cfg.DiagramDpsCurrentKa} kA";
                TextNote.Create(doc, view.Id, new XYZ(dx - RevitHelpers.M(2.6), dy - RevitHelpers.M(0.2), 0),
                    dps + (cfg.DiagramDpsOnNeutral ? " (F e N)" : " (fases)") +
                    (cfg.DiagramDpsBreaker ? $" / disj. {cfg.DiagramDpsBreakerA} A" : ""), textType);
            }

            // grupos de DR: ramal do barramento, disjuntor opcional, DR e sub-barramento
            double drX = RevitHelpers.M(0.5), subX = RevitHelpers.M(1.4);
            foreach (var group in items.Select((it, idx) => (it, idx)).Where(x => x.it.Dr >= 0).GroupBy(x => x.it.Dr))
            {
                double y0 = -s * group.Min(x => x.idx), y1 = -s * group.Max(x => x.idx);
                double yb = y0 + s * 0.45;
                Line(doc, view, new XYZ(busX, yb, 0), new XYZ(subX, yb, 0));
                if (cfg.BreakerBeforeDr) Breaker(doc, view, new XYZ(drX - RevitHelpers.M(0.25), yb, 0), vertical: false);
                Box(doc, view, new XYZ(drX + RevitHelpers.M(0.25), yb, 0), RevitHelpers.M(0.35), RevitHelpers.M(0.22));
                TextNote.Create(doc, view.Id, new XYZ(drX, yb + RevitHelpers.M(0.35), 0),
                    $"DR {DrRating(group.Select(x => x.it))} A / {cfg.DrSensitivityMa:0} mA", textType);
                Line(doc, view, new XYZ(subX, yb, 0), new XYZ(subX, y1, 0));
            }

            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                double y = -s * i;
                double start = it.Dr >= 0 ? subX : busX;
                Line(doc, view, new XYZ(start, y, 0), new XYZ(lineEnd, y, 0));
                Breaker(doc, view, new XYZ(start + RevitHelpers.M(0.8), y, 0), vertical: false);
                string text = it.Spare
                    ? $"C{it.Circuit.CircuitNumber}  RESERVA  {it.Curve}{it.Breaker:0} A"
                    : $"C{it.Circuit.CircuitNumber}  {it.Curve}{it.Breaker:0} A  {it.Wiring}  {it.Va.ToString("N0", br)} VA  {it.Circuit.LoadName}" +
                      (string.IsNullOrWhiteSpace(it.CableType) ? "" : $"  [{it.CableType}]");
                TextNote.Create(doc, view.Id, new XYZ(lineEnd + RevitHelpers.M(0.15), y + RevitHelpers.M(0.12), 0), text, textType);
            }
            return view;
        }

        /// <summary>Diagrama multifilar: um traço por condutor (fases, neutro, terra) de cada circuito.</summary>
        private static void Multifilar(Document doc, ElementId type, string panelName, List<Item> items, DimensioningSettings cfg)
        {
            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            ViewDrafting view = NewView(doc, type, $"RE - Multifilar {panelName}", cfg);
            double gap = RevitHelpers.M(0.12), row = RevitHelpers.M(1.0), x0 = RevitHelpers.M(1.5), x1 = RevitHelpers.M(5.0);
            string[] busNames = { "A", "B", "C", "N", "PE" };
            int n = items.Count;
            for (int b = 0; b < busNames.Length; b++)
            {
                double bx = b * gap * 2;
                Line(doc, view, new XYZ(bx, gap, 0), new XYZ(bx, -row * n, 0));
                TextNote.Create(doc, view.Id, new XYZ(bx - gap / 2, gap * 4, 0), busNames[b], textType);
            }
            TextNote.Create(doc, view.Id, new XYZ(0, gap * 8, 0), $"QUADRO {panelName} — DIAGRAMA MULTIFILAR", textType);

            for (int i = 0; i < n; i++)
            {
                Item it = items[i];
                double y = -row * i;
                var conductors = new List<(int Bus, string Label)>();
                int phaseStart = it.Circuit.StartSlot > 0 ? (it.Circuit.StartSlot - 1) / 2 % 3 : 0;
                for (int p = 0; p < Math.Min(3, it.Poles); p++) conductors.Add(((phaseStart + p) % 3, "F"));
                if (it.Neutral && it.Poles != 2) conductors.Add((3, "N"));
                if (it.Ground) conductors.Add((4, "PE"));
                for (int k = 0; k < conductors.Count; k++)
                {
                    double yy = y - k * gap;
                    double bx = conductors[k].Bus * gap * 2;
                    Line(doc, view, new XYZ(bx, yy, 0), new XYZ(x1, yy, 0));
                    if (conductors[k].Label == "F") Breaker(doc, view, new XYZ(x0, yy, 0), vertical: false, small: true);
                }
                TextNote.Create(doc, view.Id, new XYZ(x1 + RevitHelpers.M(0.15), y, 0),
                    it.Spare ? $"C{it.Circuit.CircuitNumber} RESERVA" : $"C{it.Circuit.CircuitNumber} {it.Curve}{it.Breaker:0} A {it.Circuit.LoadName}" +
                                                                 (it.Dr >= 0 ? $" (DR {it.Dr + 1})" : ""), textType);
            }
        }

        /// <summary>Detalhe da proteção: tabela de disjuntores, curvas, DR e capacidade de interrupção.</summary>
        private static void ProtectionDetail(Document doc, ElementId type, string panelName, List<Item> items, DimensioningSettings cfg)
        {
            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            ViewDrafting view = NewView(doc, type, $"RE - Proteção {panelName}", cfg);
            double mm = 50 / 304.8, y = 0;
            TextNote.Create(doc, view.Id, new XYZ(0, y, 0), $"QUADRO {panelName} — DETALHE DA PROTEÇÃO", textType);
            y -= 6 * mm;
            TextNote.Create(doc, view.Id, new XYZ(0, y, 0), $"Barramento {cfg.ProtectionBusType}" +
                (cfg.ProtectionShowNeutralBus ? "  N" : "") + (cfg.ProtectionShowGroundBus ? "  PE" : ""), textType);
            if (cfg.ProtectionShowSummary)
            {
                y -= 6 * mm;
                TextNote.Create(doc, view.Id, new XYZ(0, y, 0),
                    $"{items.Count} circuitos | {items.Sum(i => i.Va):0} VA | {cfg.ProtectionBreakersPerRow} disjuntores/linha", textType);
            }
            double busX = 150 * mm, busY = -18 * mm;
            if (cfg.ProtectionBusType == "DIN")
            {
                int perRow = cfg.ProtectionBreakersPerRow;
                for (int row = 0; row * perRow < items.Count; row++)
                {
                    int count = Math.Min(perRow, items.Count - row * perRow);
                    double yy = busY - row * 10 * mm;
                    Line(doc, view, new XYZ(busX, yy, 0), new XYZ(busX + count * 11 * mm, yy, 0));
                    if (cfg.ProtectionShowNeutralBus) Line(doc, view, new XYZ(busX, yy + 2 * mm, 0), new XYZ(busX + count * 11 * mm, yy + 2 * mm, 0));
                    if (cfg.ProtectionShowGroundBus) Line(doc, view, new XYZ(busX, yy + 4 * mm, 0), new XYZ(busX + count * 11 * mm, yy + 4 * mm, 0));
                    for (int slot = 0; slot < count; slot++)
                    {
                        double xx = busX + (slot + 0.5) * 11 * mm;
                        Line(doc, view, new XYZ(xx, yy, 0), new XYZ(xx, yy - 4 * mm, 0));
                    }
                }
            }
            else
            {
                int perSection = cfg.ProtectionFishboneMinCircuits;
                for (int section = 0; section * perSection < items.Count; section++)
                {
                    int count = Math.Min(perSection, items.Count - section * perSection);
                    double yy = busY - section * (perSection + 1) * 7 * mm;
                    Line(doc, view, new XYZ(busX, yy, 0), new XYZ(busX, yy - count * 7 * mm, 0));
                    if (cfg.ProtectionShowNeutralBus) Line(doc, view, new XYZ(busX + 2 * mm, yy, 0), new XYZ(busX + 2 * mm, yy - count * 7 * mm, 0));
                    if (cfg.ProtectionShowGroundBus) Line(doc, view, new XYZ(busX + 4 * mm, yy, 0), new XYZ(busX + 4 * mm, yy - count * 7 * mm, 0));
                    for (int i = 0; i < count; i++)
                    {
                        double branchY = yy - (i + 0.5) * 7 * mm;
                        double side = i % 2 == 0 ? -1 : 1;
                        Line(doc, view, new XYZ(busX, branchY, 0), new XYZ(busX + side * 9 * mm, branchY, 0));
                    }
                }
            }
            y -= 10 * mm;
            string[] head = { "Circuito", "Disjuntor", "Curva", "Polos", "DR", "Icu (kA)" };
            double[] col = { 0, 30, 55, 72, 88, 115 };
            for (int c = 0; c < head.Length; c++) TextNote.Create(doc, view.Id, new XYZ(col[c] * mm, y, 0), head[c], textType);
            foreach (Item it in items)
            {
                y -= 6 * mm;
                string[] cells =
                {
                    $"C{it.Circuit.CircuitNumber} {(it.Spare ? "RESERVA" : it.Circuit.LoadName)}",
                    $"{it.Breaker:0} A", it.Curve, it.Poles.ToString(),
                    it.Dr >= 0 ? $"DR {it.Dr + 1} ({cfg.DrSensitivityMa:0} mA)" : "—", $"{cfg.BreakerIcuKa:0.#}",
                };
                for (int c = 0; c < cells.Length; c++) TextNote.Create(doc, view.Id, new XYZ(col[c] * mm, y, 0), cells[c], textType);
            }
        }

        private static void Line(Document doc, View view, XYZ a, XYZ b) =>
            doc.Create.NewDetailCurve(view, Autodesk.Revit.DB.Line.CreateBound(a, b));

        private static void Box(Document doc, View view, XYZ c, double w, double h)
        {
            XYZ p1 = c + new XYZ(-w / 2, -h / 2, 0), p2 = c + new XYZ(w / 2, -h / 2, 0), p3 = c + new XYZ(w / 2, h / 2, 0), p4 = c + new XYZ(-w / 2, h / 2, 0);
            Line(doc, view, p1, p2); Line(doc, view, p2, p3); Line(doc, view, p3, p4); Line(doc, view, p4, p1);
        }

        /// <summary>Símbolo simples de disjuntor: um retângulo sobre a linha.</summary>
        private static void Breaker(Document doc, View view, XYZ c, bool vertical, bool small = false)
        {
            double k = small ? 0.4 : 1.0;
            Box(doc, view, c, RevitHelpers.M(vertical ? 0.15 : 0.3) * k, RevitHelpers.M(vertical ? 0.3 : 0.15) * k);
        }
    }
}
