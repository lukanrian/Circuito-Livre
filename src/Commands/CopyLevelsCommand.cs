using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
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

            List<CheckItem> viewModels = BuildViewModels(doc);
            var window = new CopyLevelsWindow(sources, existing, viewModels);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;

            List<LevelRow> rows = window.SelectedRows;
            bool updateExisting = window.UpdateExisting;
            bool allLevels = window.ViewsOnAllLevels;
            bool replace = window.ReplaceExisting;
            List<ViewModelRef> models = window.SelectedViewModels.OfType<ViewModelRef>().ToList();

            // Substituir: apaga os níveis do projeto cujo nome não está na lista da origem
            var keepNames = new HashSet<string>(window.AllRows.Select(r => r.TargetName.Trim()), StringComparer.CurrentCultureIgnoreCase);
            List<Level> toDelete = replace ? GetLevels(doc).Where(l => !keepNames.Contains(l.Name)).ToList() : new List<Level>();
            if (toDelete.Count > 0 && !ConfirmDelete(doc, toDelete)) return Result.Cancelled;

            int created = 0, updated = 0, reused = 0, views = 0, deleted = 0;
            var notes = new List<string>();
            var pendingNames = new List<(Element Element, string Name)>();
            var deleteIds = new HashSet<ElementId>(toDelete.Select(l => l.Id));

            using (var t = new Transaction(doc, replace ? "Substituir níveis" : "Copiar níveis"))
            {
                t.Start();

                List<Level> current = GetLevels(doc);
                var usedNames = new HashSet<string>(current.Select(l => l.Name), StringComparer.CurrentCultureIgnoreCase);
                var viewNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate).Select(v => v.Name), StringComparer.CurrentCultureIgnoreCase);
                var targets = new List<(Level Level, string Name)>();

                foreach (LevelRow row in rows.OrderBy(r => r.ElevationM))
                {
                    string name = row.TargetName.Trim();
                    double target = ToFeet(row.ElevationM);

                    Level sameName = current.FirstOrDefault(l => !deleteIds.Contains(l.Id) &&
                        string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase));
                    if (sameName != null)
                    {
                        if (updateExisting && Math.Abs(ToMeters(sameName.ProjectElevation) - row.ElevationM) >= ToleranceM)
                        {
                            try { SetElevation(sameName, target); updated++; }
                            catch (Exception ex) { notes.Add($"{name}: não foi possível ajustar a elevação ({ex.Message})."); }
                        }
                        else reused++;
                        targets.Add((sameName, sameName.Name));
                        continue;
                    }

                    try
                    {
                        Level level = Level.Create(doc, target);
                        SetElevation(level, target);
                        string unique = UniqueName(name, usedNames);
                        level.Name = unique;
                        usedNames.Add(unique);
                        if (unique != name) pendingNames.Add((level, name)); // nome ocupado por um nível que será apagado
                        current.Add(level);
                        targets.Add((level, name));
                        created++;
                    }
                    catch (Exception ex) { notes.Add($"{name}: {ex.Message}"); }
                }

                // "todos os níveis": também os níveis do projeto que não foram marcados (e não serão apagados)
                if (allLevels)
                    foreach (Level l in current.Where(l => !deleteIds.Contains(l.Id) && targets.All(x => x.Level.Id != l.Id)))
                        targets.Add((l, l.Name));

                // vistas a partir dos modelos escolhidos
                var planned = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                foreach (var (level, levelName) in targets.OrderBy(x => x.Level.ProjectElevation))
                {
                    foreach (ViewModelRef m in models)
                    {
                        string viewName = m.NameFor(levelName);
                        // dois modelos da mesma série (ex.: "... - TÉRREO" e "... - SUPERIOR") geram o mesmo nome: cria uma vez só
                        if (!planned.Add(viewName)) continue;
                        if (viewNames.Contains(viewName) && !ViewOnDeletedLevel(doc, viewName, deleteIds))
                        {
                            notes.Add($"\"{viewName}\" já existe — não recriada.");
                            continue;
                        }
                        try
                        {
                            View view = CreateFromModel(doc, m, level);
                            if (viewNames.Contains(viewName)) pendingNames.Add((view, viewName)); // o nome volta a ficar livre depois de apagar
                            else
                            {
                                try { view.Name = viewName; } catch (Exception) { }
                                viewNames.Add(viewName);
                            }
                            views++;
                        }
                        catch (Exception ex) { notes.Add($"{levelName} / {m.ViewName}: {ex.Message}"); }
                    }
                }

                // apaga os níveis antigos (com as vistas e elementos ligados a eles)
                foreach (Level l in toDelete)
                {
                    string n = l.Name;
                    try { doc.Delete(l.Id); deleted++; }
                    catch (Exception ex) { notes.Add($"Nível \"{n}\" não foi apagado: {ex.Message}"); }
                }

                // nomes que estavam ocupados pelos níveis/vistas apagados
                if (pendingNames.Count > 0)
                {
                    doc.Regenerate();
                    foreach (var (element, name) in pendingNames)
                    {
                        if (!element.IsValidObject) continue;
                        try { element.Name = name; }
                        catch (Exception) { notes.Add($"Não foi possível renomear \"{element.Name}\" para \"{name}\" (nome em uso)."); }
                    }
                }

                t.Commit();
            }

            var report = new TaskDialog("Copiar níveis")
            {
                MainInstruction = $"{created} nível(is) criado(s), {views} vista(s) criada(s)" + (replace ? $", {deleted} nível(is) antigo(s) apagado(s)." : "."),
                MainContent = $"Ajustados: {updated}   •   Já existentes usados: {reused}   •   Modelos de vista: {models.Count}",
            };
            if (notes.Count > 0) report.ExpandedContent = RevitHelpers.Join(notes, 40);
            report.Show();
            return Result.Succeeded;
        }

        private static bool ViewOnDeletedLevel(Document doc, string name, HashSet<ElementId> deleteIds) =>
            deleteIds.Count > 0 && new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                          v.GenLevel != null && deleteIds.Contains(v.GenLevel.Id));

        /// <summary>Mostra o que será apagado (vistas e elementos ligados a cada nível) e pede confirmação.</summary>
        private static bool ConfirmDelete(Document doc, List<Level> levels)
        {
            var lines = new List<string>();
            int totalElements = 0, totalViews = 0;
            foreach (Level l in levels)
            {
                int planViews = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Count(v => !v.IsTemplate && v.GenLevel?.Id == l.Id);
                int elems = new FilteredElementCollector(doc).WherePasses(new ElementLevelFilter(l.Id))
                    .WhereElementIsNotElementType().Count(e => !(e is View));
                totalElements += elems;
                totalViews += planViews;
                lines.Add($"• {l.Name}: {planViews} vista(s), {elems} elemento(s)");
            }

            var dialog = new TaskDialog("Substituir níveis")
            {
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = $"Apagar {levels.Count} nível(is) existente(s)?",
                MainContent = "Os níveis abaixo não estão na origem e serão apagados DEPOIS de criar os novos níveis e as vistas. " +
                              "O Revit apaga junto as vistas desses níveis e os elementos ligados a eles" +
                              (totalElements > 0 ? $" ({totalElements} elemento(s)!)" : "") +
                              ". Dá para desfazer com Ctrl+Z logo em seguida.\n\n" + string.Join("\n", lines.Take(30)),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            return dialog.Show() == TaskDialogResult.Yes;
        }

        // ------------------------------------------------------------ modelos de vista

        /// <summary>Uma vista existente usada como modelo para as novas vistas de cada nível.</summary>
        public class ViewModelRef
        {
            public ElementId ViewId;
            public string Label;
            public string ViewName;
            public string LevelName;

            /// <summary>
            /// Nome da nova vista: troca, no nome do modelo, o nome do nível do modelo (ou o trecho depois do último " - ")
            /// pelo nome do novo nível; mantém maiúsculas se o modelo estiver em maiúsculas.
            /// </summary>
            public string NameFor(string level)
            {
                // nome do nível do modelo sem sufixos como " (N.A.)": "Térreo (N.A.)" → "Térreo"
                var tokens = new List<string>();
                if (!string.IsNullOrWhiteSpace(LevelName))
                {
                    tokens.Add(LevelName.Trim());
                    string baseName = System.Text.RegularExpressions.Regex.Replace(LevelName, @"\s*\(.*?\)\s*$", "").Trim();
                    if (baseName.Length > 0 && baseName != tokens[0]) tokens.Add(baseName);
                }
                foreach (string token in tokens)
                {
                    int i = ViewName.IndexOf(token, StringComparison.CurrentCultureIgnoreCase);
                    if (i < 0) continue;
                    string found = ViewName.Substring(i, token.Length);
                    return ViewName.Substring(0, i) + Styled(level, found) + ViewName.Substring(i + token.Length);
                }
                // sem o nome do nível no nome da vista: troca o trecho depois do último " - "
                int dash = ViewName.LastIndexOf(" - ", StringComparison.Ordinal);
                string tail = dash > 0 ? ViewName.Substring(dash + 3) : ViewName;
                return dash > 0 ? ViewName.Substring(0, dash + 3) + Styled(level, tail) : $"{ViewName} - {Styled(level, ViewName)}";
            }

            /// <summary>Usa maiúsculas se o trecho trocado do modelo estava em maiúsculas.</summary>
            private static string Styled(string level, string sample)
            {
                bool upper = sample.Any(char.IsLetter) && sample == sample.ToUpperInvariant();
                return upper ? level.ToUpperInvariant() : level;
            }
        }

        /// <summary>Plantas de piso/forro existentes, com o caminho de pastas do navegador de projeto.</summary>
        private static List<CheckItem> BuildViewModels(Document doc)
        {
            BrowserOrganization org = null;
            try { org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc); } catch (Exception) { }

            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan) &&
                            v.GetPrimaryViewId() == ElementId.InvalidElementId)
                .Select(v =>
                {
                    string folder = "";
                    try
                    {
                        if (org != null)
                            folder = string.Join(" › ", org.GetFolderItems(v.Id).Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
                    }
                    catch (Exception) { }
                    var m = new ViewModelRef { ViewId = v.Id, ViewName = v.Name, LevelName = v.GenLevel.Name };
                    m.Label = (folder.Length > 0 ? folder + " › " : "") + v.Name + "   →   ex.: " + m.NameFor("NOVO NÍVEL");
                    return m;
                })
                .OrderBy(m => m.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new CheckItem(m.Label, m))
                .ToList();
        }

        private static readonly BuiltInParameter[] SkipParams =
        {
            BuiltInParameter.VIEW_NAME, BuiltInParameter.PLAN_VIEW_LEVEL, BuiltInParameter.VIEW_TEMPLATE,
            BuiltInParameter.VIEW_DESCRIPTION, BuiltInParameter.VIEW_DEPENDENCY,
        };

        /// <summary>Cria a vista do nível copiando tipo, modelo de vista, faixa de vista e parâmetros do modelo.</summary>
        private static View CreateFromModel(Document doc, ViewModelRef m, Level level)
        {
            var model = (ViewPlan)doc.GetElement(m.ViewId);
            ViewPlan plan = ViewPlan.Create(doc, model.GetTypeId(), level.Id);

            if (model.ViewTemplateId != ElementId.InvalidElementId)
                try { plan.ViewTemplateId = model.ViewTemplateId; } catch (Exception) { }

            // parâmetros de instância (organização do navegador, disciplina, escala, nível de detalhe...)
            var skip = new HashSet<long>(SkipParams.Select(b => (long)b));
            foreach (Parameter src in model.Parameters)
            {
                if (src.IsReadOnly || !src.HasValue || src.Definition == null) continue;
                if (src.Definition is InternalDefinition idef && skip.Contains((long)idef.BuiltInParameter)) continue;
                Parameter dst = src.IsShared ? plan.get_Parameter(src.GUID) : plan.get_Parameter(src.Definition);
                if (dst == null || dst.IsReadOnly) continue;
                try
                {
                    switch (src.StorageType)
                    {
                        case StorageType.String: dst.Set(src.AsString() ?? ""); break;
                        case StorageType.Integer: dst.Set(src.AsInteger()); break;
                        case StorageType.Double: dst.Set(src.AsDouble()); break;
                        case StorageType.ElementId: dst.Set(src.AsElementId()); break;
                    }
                }
                catch (Exception) { /* parâmetro controlado pelo modelo de vista ou incompatível */ }
            }

            // faixa de vista: o que estava amarrado ao nível do modelo passa para o novo nível
            try
            {
                PlanViewRange range = model.GetViewRange();
                foreach (PlanViewPlane plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                {
                    ElementId lid = range.GetLevelId(plane);
                    if (lid == model.GenLevel.Id) range.SetLevelId(plane, level.Id);
                }
                plan.SetViewRange(range);
            }
            catch (Exception) { }

            return plan;
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
