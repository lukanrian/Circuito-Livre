using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>Abre as configurações do plugin (salvas em %AppData%\RafaelElectrical\dimensionamento.json).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var window = new SettingsWindow(DimensioningSettings.Load());
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;
            window.Settings.Save();
            return Result.Succeeded;
        }
    }
}
