using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Abre as configurações do plugin. Ficam no computador (%AppData%\RafaelElectrical\dimensionamento.json)
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
                context.TagOptions = TagCatalog.Options(doc);
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
                using var t = new Transaction(doc, "Configurações do RafaelElectrical");
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
