using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;

namespace CircuitoLivre.Commands
{
    /// <summary>Versão do plugin, licença e links do projeto.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        private const string Repository = "https://github.com/lukanrian/Plugin-el-trico-rvt";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var dialog = new TaskDialog("Sobre o Circuito Livre")
            {
                MainInstruction = $"Circuito Livre {RevitHelpers.PluginVersion}",
                MainContent = "Plugin elétrico gratuito e de código aberto para Revit (licença GPL-3.0), com dimensionamento pela ABNT NBR 5410.\n\n" +
                              "Versão beta: confira os resultados. A responsabilidade técnica do projeto é do profissional que o assina.",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir o projeto no GitHub", "Código, novidades e versões para baixar.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Ver as versões (releases)");

            string url = dialog.Show() switch
            {
                TaskDialogResult.CommandLink1 => Repository,
                TaskDialogResult.CommandLink2 => Repository + "/releases",
                _ => null,
            };
            if (url != null)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { TaskDialog.Show("Sobre", url); }
            }
            return Result.Succeeded;
        }
    }
}
