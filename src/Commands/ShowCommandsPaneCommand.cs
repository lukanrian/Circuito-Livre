using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>Mostra o painel lateral com todos os comandos do plugin.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowCommandsPaneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = data.Application.GetDockablePane(CommandsPane.Id);
                pane?.Show();
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("RafaelElectrical", "O painel não está disponível: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
