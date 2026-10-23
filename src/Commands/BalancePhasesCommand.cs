using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Selection;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Balanceamento de fases de um quadro trifásico: redistribui os circuitos monofásicos (1 polo) entre as
    /// posições do quadro para que as cargas das fases A, B e C fiquem o mais próximas possível.
    /// Circuitos de 2 e 3 polos e circuitos travados ficam onde estão (a carga deles entra na conta).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class BalancePhasesCommand : IExternalCommand
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly string[] PhaseName = { "A", "B", "C" };

        private class Item
        {
            public ElectricalSystem Circuit;
            public double VA;
            public int Slot;
            public int Target;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            FamilyInstance panel;
            try
            {
                panel = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<FamilyInstance>()
                            .FirstOrDefault(CircuitDeviceFilter.IsPanel)
                        ?? (FamilyInstance)doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element, new PanelFilter(), "Selecione o quadro a balancear"));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            string panelName = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name;
            List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment?.Id == panel.Id && c.SystemType == ElectricalSystemType.PowerCircuit)
                .ToList();
            if (circuits.Count == 0)
            {
                TaskDialog.Show("Balancear fases", $"O quadro \"{panelName}\" não tem circuitos de força.");
                return Result.Cancelled;
            }

            int maxSlot = RenumberCircuitsCommand.GetMaxSlot(panel);
            PanelScheduleView existing = new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
                .FirstOrDefault(v => !v.IsPanelScheduleTemplate() && v.GetPanel() == panel.Id);
            Func<int, int> phaseOf = DetectPhasePattern(circuits);

            // carga fixa (multipolares e travados) e circuitos móveis
            var fixedLoad = new double[3];
            var movable = new List<Item>();
            var occupiedFixed = new HashSet<int>();
            foreach (ElectricalSystem c in circuits)
            {
                double va = Va(c);
                int poles = Math.Max(1, Safe(() => c.PolesNumber));
                int start = Safe(() => c.StartSlot);
                bool locked = !IsRealCircuit(c) || IsLocked(existing, start);
                if (poles == 1 && !locked && start > 0)
                {
                    movable.Add(new Item { Circuit = c, VA = va, Slot = start });
                    continue;
                }
                for (int i = 0; i < poles; i++)
                {
                    int slot = start + SlotStep(phaseOf) * i;
                    occupiedFixed.Add(slot);
                    fixedLoad[phaseOf(slot)] += va / poles;
                }
            }

            if (movable.Count < 2)
            {
                TaskDialog.Show("Balancear fases", "Não há circuitos monofásicos suficientes para redistribuir.");
                return Result.Cancelled;
            }

            double[] before = (double[])fixedLoad.Clone();
            foreach (Item it in movable) before[phaseOf(it.Slot)] += it.VA;

            // posições disponíveis: as que os monofásicos ocupam hoje + as vagas até a última posição usada (+3)
            int lastUsed = Math.Max(movable.Max(m => m.Slot), occupiedFixed.DefaultIfEmpty(0).Max());
            int limit = Math.Min(maxSlot, lastUsed + 3);
            var free = new List<int>[3] { new List<int>(), new List<int>(), new List<int>() };
            for (int s = 1; s <= limit; s++)
                if (!occupiedFixed.Contains(s)) free[phaseOf(s)].Add(s);

            // maior carga primeiro, na fase menos carregada que ainda tem posição livre (LPT)
            double[] after = (double[])fixedLoad.Clone();
            foreach (Item it in movable.OrderByDescending(m => m.VA))
            {
                int best = Enumerable.Range(0, 3).Where(p => free[p].Count > 0).OrderBy(p => after[p]).ThenBy(p => free[p][0]).FirstOrDefault(-1);
                if (best < 0) { it.Target = it.Slot; after[phaseOf(it.Slot)] += it.VA; continue; }
                // mantém o circuito na posição atual se ela for da fase escolhida
                int pick = free[best].Contains(it.Slot) ? it.Slot : free[best][0];
                free[best].Remove(pick);
                it.Target = pick;
                after[best] += it.VA;
            }

            string report = Report(before, after);
            var confirm = new TaskDialog("Balancear fases")
            {
                MainInstruction = $"Quadro {panelName}",
                MainContent = report + $"\n\n{movable.Count(m => m.Target != m.Slot)} circuito(s) monofásico(s) mudariam de posição. " +
                              "Multipolares e circuitos travados não se movem. A numeração dos circuitos muda junto com a posição.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok,
            };
            if (confirm.Show() != TaskDialogResult.Ok) return Result.Cancelled;

            int moved = 0;
            var failed = new List<string>();
            using (var t = new Transaction(doc, "Balancear fases"))
            {
                t.Start();
                PanelScheduleView schedule = RenumberCircuitsCommand.GetOrCreateSchedule(doc, panel);
                if (schedule == null)
                {
                    t.RollBack();
                    TaskDialog.Show("Balancear fases", "Não foi possível obter a tabela de quadro (Panel Schedule).");
                    return Result.Failed;
                }

                foreach (Item it in movable.Where(m => m.Target != m.Slot).OrderBy(m => m.Target))
                {
                    doc.Regenerate();
                    int current = it.Circuit.StartSlot;
                    if (current == it.Target) continue;
                    try
                    {
                        if (RenumberCircuitsCommand.TryGetCell(schedule, current, out int r1, out int c1) &&
                            RenumberCircuitsCommand.TryGetCell(schedule, it.Target, out int r2, out int c2) &&
                            schedule.CanMoveSlotTo(r1, c1, r2, c2))
                        {
                            schedule.MoveSlotTo(r1, c1, r2, c2);
                            moved++;
                        }
                        else failed.Add($"{it.Circuit.CircuitNumber} — {it.Circuit.LoadName}");
                    }
                    catch (Exception) { failed.Add($"{it.Circuit.CircuitNumber} — {it.Circuit.LoadName}"); }
                }
                t.Commit();
            }

            var done = new TaskDialog("Balancear fases")
            {
                MainInstruction = $"{moved} circuito(s) reposicionado(s) em {panelName}.",
                MainContent = "Previsto:\n" + report + "\n\nConfira as cargas por fase na tabela do quadro.",
            };
            if (failed.Count > 0) done.ExpandedContent = "Não movidos:\n" + string.Join("\n", failed);
            done.Show();
            return Result.Succeeded;
        }

        /// <summary>
        /// Fase de cada posição. Revit numera as fases em sequência pelas posições: em quadros de duas colunas
        /// (numeração "atravessada") as posições 1-2 são A, 3-4 B, 5-6 C; em uma coluna 1 A, 2 B, 3 C.
        /// Quando os circuitos informam a carga por fase, o padrão é conferido com eles.
        /// </summary>
        private static Func<int, int> DetectPhasePattern(List<ElectricalSystem> circuits)
        {
            Func<int, int> twoCol = s => ((s - 1) / 2) % 3;
            Func<int, int> oneCol = s => (s - 1) % 3;
            int scoreTwo = 0, scoreOne = 0;
            foreach (ElectricalSystem c in circuits)
            {
                if (Safe(() => c.PolesNumber) != 1) continue;
                int phase = MeasuredPhase(c);
                if (phase < 0) continue;
                int slot = Safe(() => c.StartSlot);
                if (twoCol(slot) == phase) scoreTwo++;
                if (oneCol(slot) == phase) scoreOne++;
            }
            return scoreOne > scoreTwo ? oneCol : twoCol;
        }

        private static int SlotStep(Func<int, int> phaseOf) => phaseOf(2) == phaseOf(1) ? 2 : 1;

        /// <summary>Fase em que o Revit colocou a carga do circuito (−1 se não der para ler).</summary>
        private static int MeasuredPhase(ElectricalSystem c)
        {
            // nomes resolvidos em tempo de execução (variam entre versões da API)
            string[] names = { "RBS_ELEC_APPARENT_LOAD_PHASEA", "RBS_ELEC_APPARENT_LOAD_PHASEB", "RBS_ELEC_APPARENT_LOAD_PHASEC" };
            int found = -1;
            for (int i = 0; i < 3; i++)
            {
                if (!Enum.TryParse(names[i], out BuiltInParameter bip)) return -1;
                double v = 0;
                try { v = c.get_Parameter(bip)?.AsDouble() ?? 0; } catch (Exception) { }
                if (v > 1e-6) { if (found >= 0) return -1; found = i; }
            }
            return found;
        }

        /// <summary>Posição travada na tabela de quadro (só dá para saber se a tabela já existe).</summary>
        private static bool IsLocked(PanelScheduleView schedule, int slot)
        {
            if (schedule == null || slot <= 0) return false;
            try
            {
                return RenumberCircuitsCommand.TryGetCell(schedule, slot, out int row, out int col) && schedule.IsSlotLocked(row, col);
            }
            catch (Exception) { return false; }
        }

        /// <summary>Reservas e espaços não são redistribuídos.</summary>
        private static bool IsRealCircuit(ElectricalSystem c)
        {
            try
            {
                object type = c.GetType().GetProperty("CircuitType")?.GetValue(c);
                return type == null || type.ToString() == "Circuit";
            }
            catch (Exception) { return true; }
        }

        private static double Va(ElectricalSystem c) =>
            Safe(() => UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes));

        private static string Report(double[] before, double[] after)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Fase      Antes (VA)     Depois (VA)");
            for (int i = 0; i < 3; i++)
                sb.AppendLine($"{PhaseName[i],-6}{before[i].ToString("N0", Br),14}{after[i].ToString("N0", Br),16}");
            sb.Append($"Desequilíbrio: {Imbalance(before):0.0} % → {Imbalance(after):0.0} %");
            return sb.ToString();
        }

        /// <summary>(maior − menor) / maior, em %.</summary>
        private static double Imbalance(double[] p)
        {
            double max = p.Max(), min = p.Min();
            return max > 0 ? (max - min) / max * 100 : 0;
        }

        private static T Safe<T>(Func<T> f)
        {
            try { return f(); } catch (Exception) { return default; }
        }
    }
}
