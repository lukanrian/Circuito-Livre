using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Dimensionar projeto: hierarquia de quadros com carga instalada, demanda (fatores da concessionária nas
    /// Configurações), corrente do alimentador, curto-circuito estimado e categoria do padrão de entrada.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DimensionProjectCommand : IExternalCommand
    {
        private const string Title = "Dimensionar projeto";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) return Result.Cancelled;

            DimensioningSettings cfg = DimensioningSettings.Load();
            var service = new ProjectSizingService(doc, cfg);
            service.Run();
            if (service.Rows.Count == 0)
            {
                TaskDialog.Show(Title, "Nenhum quadro encontrado no projeto.");
                return Result.Cancelled;
            }

            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            string[] headers = { "Quadro", "Alimentado por", "Instalada (VA)", "Ilum.+TUG (VA)", "TUE (VA)", "Demanda (VA)", "Fator", "I alim. (A)", "Icc (kA)", "Situação" };
            List<IList<string>> rows = service.Rows.Select(r => (IList<string>)new[]
            {
                new string(' ', r.Depth * 4) + r.Name, r.Parent ?? "(origem)",
                r.InstalledVa.ToString("N0", br), r.LightingTugVa.ToString("N0", br), r.TueVa.ToString("N0", br),
                r.DemandVa.ToString("N0", br), r.Factor.ToString("0.00", br), r.FeederCurrentA.ToString("0.0", br),
                double.IsFinite(r.IccKa) ? r.IccKa.ToString("0.00", br) : "Não verificado", r.Status,
            }).ToList();

            string summary = $"Demanda total: {service.TotalDemandVa.ToString("N0", br)} VA  →  padrão de entrada: {service.Entrance}." +
                             (!cfg.RegisterUtility || string.IsNullOrWhiteSpace(cfg.Utility) ? "" : $"  Concessionária: {cfg.Utility} ({cfg.SupplySystem}).") +
                             "\nFatores de demanda e categorias de atendimento editáveis em Configurações → Concessionária. " +
                             "Os valores iniciais são exemplos, não uma tabela homologada da concessionária; confira antes de usar o padrão de entrada. " +
                             "Curto-circuito estimado só pela resistência dos alimentadores (confira com o cálculo completo).";

            var window = new TableWindow(Title, summary, headers, rows, 1150)
                .Action("Gravar nos quadros", true)
                .Action("Exportar Excel");
            RevitHelpers.ShowOwned(window, data);
            window.ShowDialog();

            if (window.Chosen == "Exportar Excel")
            {
                var d = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Dimensionamento_Projeto.xlsx" };
                if (d.ShowDialog() != true) return Result.Cancelled;
                var sheet = new List<IList<object>> { headers.Cast<object>().ToList() };
                sheet.AddRange(service.Rows.Select(r => (IList<object>)new object[]
                {
                    new string(' ', r.Depth * 4) + r.Name, r.Parent ?? "(origem)", Math.Round(r.InstalledVa), Math.Round(r.LightingTugVa),
                    Math.Round(r.TueVa), Math.Round(r.DemandVa), Math.Round(r.Factor, 3), Math.Round(r.FeederCurrentA, 1), double.IsFinite(r.IccKa) ? (object)Math.Round(r.IccKa, 2) : "Não verificado", r.Status,
                }));
                sheet.Add(new object[] { });
                sheet.Add(new object[] { "Demanda total (VA)", Math.Round(service.TotalDemandVa), "Padrão de entrada", service.Entrance });
                new XlsxWriter().Sheet("Projeto", sheet).Save(d.FileName);
                TaskDialog.Show(Title, "Planilha exportada:\n" + d.FileName);
                return Result.Succeeded;
            }
            if (window.Chosen != "Gravar nos quadros") return Result.Cancelled;

            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                SharedParameters.Ensure(doc, SharedParameters.PanelDemand, SharedParameters.PanelIcc, SharedParameters.PanelEntrance, SharedParameters.PanelIccStatus);
                doc.Regenerate();
                foreach (var r in service.Rows)
                {
                    bool written = SharedParameters.SetNumber(r.Panel, SharedParameters.PanelDemand, Math.Round(r.DemandVa));
                    written &= SharedParameters.SetNumber(r.Panel, SharedParameters.PanelIcc, double.IsFinite(r.IccKa) ? Math.Round(r.IccKa, 2) : 0);
                    written &= SharedParameters.SetText(r.Panel, SharedParameters.PanelIccStatus,
                        double.IsFinite(r.IccKa) ? "Estimativa resistiva trifásica — " + r.Status : "Não verificado — " + r.Status);
                    if (r.Depth == 0) written &= SharedParameters.SetText(r.Panel, SharedParameters.PanelEntrance, service.Entrance);
                    if (!written)
                    {
                        t.RollBack();
                        TaskDialog.Show(Title, "Não foi possível gravar todos os parâmetros. Nenhuma alteração aplicada.");
                        return Result.Failed;
                    }
                }
                if (t.Commit() != TransactionStatus.Committed) return Result.Failed;
            }
            TaskDialog.Show(Title, "Gravado nos quadros: CL_Demanda (VA), CL_Icc (kA), CL_Verificação de curto e, no quadro geral, CL_Padrão de entrada.");
            return Result.Succeeded;
        }
    }
}
