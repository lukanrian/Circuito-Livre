using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Cria no projeto os níveis de um vínculo (Revit ou IFC vinculado) ou de um arquivo .ifc.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CopyLevelsCommand : IExternalCommand
    {
        private const double ToleranceM = 0.001;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Copiar níveis", "Abra um projeto (não uma família) para usar este comando.");
                return Result.Cancelled;
            }

            // Origens: todos os vínculos carregados (RVT e IFC vinculados aparecem como RevitLinkInstance)
            var sources = new List<LevelSource>();
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue; // vínculo descarregado

                double dz = link.GetTotalTransform().Origin.Z;
                List<SourceLevel> levels = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Select(l => new SourceLevel(l.Name, ToMeters(l.ProjectElevation + dz)))
                    .OrderBy(l => l.ElevationM)
                    .ToList();

                if (levels.Count > 0)
                    sources.Add(new LevelSource($"Vínculo: {link.Name}", levels));
            }

            List<ExistingLevel> existing = GetLevels(doc)
                .Select(l => new ExistingLevel(l.Name, ToMeters(l.ProjectElevation)))
                .ToList();

            var window = new CopyLevelsWindow(sources, existing);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;

            List<LevelRow> rows = window.SelectedRows;
            bool createPlans = window.CreatePlans;
            bool updateExisting = window.UpdateExisting;

            ViewFamilyType planType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);

            int created = 0, updated = 0, skipped = 0, plans = 0;
            var notes = new List<string>();

            using (var t = new Transaction(doc, "Copiar níveis"))
            {
                t.Start();

                List<Level> current = GetLevels(doc);
                var usedNames = new HashSet<string>(current.Select(l => l.Name), StringComparer.CurrentCultureIgnoreCase);

                foreach (LevelRow row in rows.OrderBy(r => r.ElevationM))
                {
                    string name = row.TargetName.Trim();
                    double target = ToFeet(row.ElevationM);

                    Level sameName = current.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase));
                    if (sameName != null)
                    {
                        if (updateExisting && Math.Abs(ToMeters(sameName.ProjectElevation) - row.ElevationM) >= ToleranceM)
                        {
                            try
                            {
                                SetElevation(sameName, target);
                                updated++;
                            }
                            catch (Exception ex) { notes.Add($"{name}: não foi possível ajustar a elevação ({ex.Message})."); }
                        }
                        else
                        {
                            skipped++;
                        }
                        continue;
                    }

                    try
                    {
                        Level level = Level.Create(doc, target);
                        SetElevation(level, target);
                        level.Name = UniqueName(name, usedNames);
                        usedNames.Add(level.Name);
                        current.Add(level);
                        created++;

                        if (createPlans && planType != null)
                        {
                            try
                            {
                                ViewPlan plan = ViewPlan.Create(doc, planType.Id, level.Id);
                                try { plan.Name = level.Name; } catch (Exception) { /* nome já usado: mantém o padrão */ }
                                plans++;
                            }
                            catch (Exception ex) { notes.Add($"{level.Name}: nível criado, mas a planta falhou ({ex.Message})."); }
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"{name}: {ex.Message}");
                    }
                }

                t.Commit();
            }

            var report = new TaskDialog("Copiar níveis")
            {
                MainInstruction = $"{created} nível(is) criado(s)" + (createPlans ? $", {plans} planta(s) de piso." : "."),
                MainContent = $"Ajustados: {updated}   •   Já existentes (ignorados): {skipped}",
            };
            if (createPlans && planType == null)
                notes.Add("Nenhum tipo de vista de planta de piso encontrado no projeto; as plantas não foram criadas.");
            if (notes.Count > 0) report.ExpandedContent = string.Join("\n", notes);
            report.Show();

            return Result.Succeeded;
        }

        private static List<Level> GetLevels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();

        /// <summary>Ajusta o nível para que a cota em relação à origem interna fique igual ao alvo.</summary>
        private static void SetElevation(Level level, double targetProjectElevationFt)
        {
            double delta = targetProjectElevationFt - level.ProjectElevation;
            if (Math.Abs(delta) > 1e-9) level.Elevation += delta;
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            if (!used.Contains(name)) return name;
            for (int i = 2; ; i++)
            {
                string candidate = $"{name} ({i})";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        private static double ToMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        private static double ToFeet(double meters) => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
    }
}
