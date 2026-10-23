using System.Collections.Generic;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Mostra todos os circuitos de força do modelo com cargas, correntes e disjuntores.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class LoadReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<CircuitRow> rows = CircuitReportService.Collect(doc);
            if (rows.Count == 0)
            {
                TaskDialog.Show("Relatório de cargas", "Nenhum circuito de força encontrado neste modelo.");
                return Result.Cancelled;
            }

            var window = new LoadReportWindow(rows, doc.Title);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            window.ShowDialog();

            if (window.SelectedCircuitId != null)
            {
                var ids = new List<ElementId> { window.SelectedCircuitId };
                uidoc.Selection.SetElementIds(ids);
                try { uidoc.ShowElements(ids); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }

            return Result.Succeeded;
        }
    }
}
