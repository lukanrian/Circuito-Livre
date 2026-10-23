using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>Mostra o painel modeless de lançamento de eletrodutos.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowConduitBuilderPaneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = data.Application.GetDockablePane(ConduitBuilderPane.Id);
                pane?.Show();
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Circuito Livre", "O painel não está disponível: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
