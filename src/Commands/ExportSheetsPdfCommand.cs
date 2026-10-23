using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>Exporta folhas selecionadas como PDFs individuais usando as opções de PDF do Revit.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportSheetsPdfCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            List<ViewSheet> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>().Where(s => !s.IsPlaceholder && s.CanBePrinted)
                .OrderBy(s => s.SheetNumber).ToList();
            if (sheets.Count == 0)
            {
                TaskDialog.Show("Exportar PDFs", "Não há pranchas imprimíveis no projeto.");
                return Result.Cancelled;
            }

            var items = sheets.Select(s => new CheckItem($"{s.SheetNumber} — {s.Name}", s, true)).ToList();
            var window = new ChecklistWindow("Exportar PDFs", "Marque as pranchas para gerar um PDF por folha.", items, "Escolher pasta");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;
            List<ViewSheet> selected = window.CheckedItems.Select(i => (ViewSheet)i.Tag).ToList();
            if (selected.Count == 0) return Result.Cancelled;

            var picker = new SaveFileDialog
            {
                Title = "Escolha a pasta dos PDFs",
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = "Pranchas.pdf",
                AddExtension = true,
                OverwritePrompt = false,
            };
            if (picker.ShowDialog() != true) return Result.Cancelled;
            string folder = Path.GetDirectoryName(picker.FileName);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return Result.Cancelled;

            int exported = 0;
            var failures = new List<string>();
            foreach (ViewSheet sheet in selected)
            {
                string name = SafeFileName($"{sheet.SheetNumber} - {sheet.Name}");
                string file = Path.Combine(folder, name + ".pdf");
                if (File.Exists(file))
                {
                    failures.Add($"{sheet.SheetNumber}: arquivo já existe");
                    continue;
                }
                try
                {
                    PDFExportOptions options = ExportPDFSettings.GetActivePredefinedSettings(doc)?.GetOptions()
                        ?? new PDFExportOptions();
                    options.Combine = true;
                    options.FileName = name;
                    if (doc.Export(folder, new List<ElementId> { sheet.Id }, options)) exported++;
                    else failures.Add($"{sheet.SheetNumber}: exportação recusada pelo Revit");
                }
                catch (Exception ex) { failures.Add($"{sheet.SheetNumber}: {ex.Message}"); }
            }

            TaskDialog.Show("Exportar PDFs", $"{exported}/{selected.Count} PDF(s) gerado(s) em {folder}." +
                (failures.Count == 0 ? "" : "\n\n" + RevitHelpers.Join(failures, 20)));
            return exported > 0 ? Result.Succeeded : Result.Failed;
        }

        private static string SafeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Trim().TrimEnd('.');
        }
    }
}
