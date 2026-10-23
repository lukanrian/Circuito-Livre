using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Abre as configurações do plugin. Ficam no computador (%AppData%\CircuitoLivre\dimensionamento.json)
    /// ou, se o usuário marcar, dentro do projeto ativo (vão junto com o .rvt).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            bool projectAvailable = doc != null && !doc.IsFamilyDocument && !doc.IsReadOnly;
            string projectJson = projectAvailable ? ProjectStorage.Get(doc, ProjectStorage.SettingsField) : null;
            bool inProject = projectJson != null;

            DimensioningSettings current = DimensioningSettings.FromJson(projectJson) ?? DimensioningSettings.LoadComputer();
            var context = new SettingsContext();
            if (doc != null && !doc.IsFamilyDocument)
            {
                context.ConduitTypes = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Electrical.ConduitType))
                    .Select(t => t.Name).Distinct().OrderBy(n => n).ToList();
                context.LevelNames = RevitHelpers.Levels(doc).Select(l => l.Name).ToList();
                context.Levels = RevitHelpers.Levels(doc).Select(l => new LevelOption
                {
                    Name = l.Name, ElevationM = RevitHelpers.ToM(l.ProjectElevation)
                }).ToList();
                context.PlanViewsByLevel = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.GenLevel != null)
                    .GroupBy(v => v.GenLevel.Name)
                    .ToDictionary(g => g.Key, g => g.Select(v => v.Name).OrderBy(n => n).ToList());
                context.CircuitLoadNames = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Electrical.ElectricalSystem))
                    .Cast<Autodesk.Revit.DB.Electrical.ElectricalSystem>()
                    .Select(c => c.LoadName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n).ToList();
                context.TagOptions = TagCatalog.Options(doc);
                var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(s => s.Category != null).ToList();
                context.DeviceTags = TagCatalog.Map.Where(m => m.Device != BuiltInCategory.OST_Conduit &&
                    m.Device != BuiltInCategory.OST_CableTray).SelectMany(m => symbols
                    .Where(s => s.Category.BuiltInCategory == m.Device)
                    .Select(s => new TagDeviceOption
                    {
                        Category = m.Label,
                        Family = TagCatalog.Label(s),
                        Key = $"{m.Device}|{TagCatalog.Label(s)}",
                        Options = context.TagOptions[m.Label]
                    })).OrderBy(x => x.Category).ThenBy(x => x.Family).ToList();
            }
            var window = new SettingsWindow(current, projectAvailable ? inProject : (bool?)null, context);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            if (!projectAvailable || (!window.SaveInProject && !inProject))
            {
                window.Settings.SaveComputer();
                return Result.Succeeded;
            }

            try
            {
                using var t = new Transaction(doc, "Configurações do Circuito Livre");
                t.Start();
                if (window.SaveInProject)
                {
                    string json = window.Settings.ToJson();
                    ProjectStorage.Set(doc, ProjectStorage.SettingsField, json);
                    DimensioningSettings.ActiveProjectJson = json;
                }
                else
                {
                    // desmarcou: o projeto volta a usar as configurações do computador (que recebem as desta janela)
                    ProjectStorage.Set(doc, ProjectStorage.SettingsField, null);
                    DimensioningSettings.ActiveProjectJson = null;
                    window.Settings.SaveComputer();
                }
                DimensioningSettings.ProjectDirty = false;
                t.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Configurações", "Não foi possível gravar no projeto:\n" + ex.Message +
                                                 "\n\nAs configurações foram salvas neste computador.");
                window.Settings.SaveComputer();
            }
            return Result.Succeeded;
        }
    }
}
