using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>
    /// Compõe pranchas: uma prancha por vista selecionada (plantas, diagramas, tabelas e tabelas de quadro),
    /// com a folha escolhida, numeração sequencial e a vista centralizada.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ComposeSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            List<FamilySymbol> titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
            if (titleBlocks.Count == 0)
            {
                TaskDialog.Show("Compor pranchas", "Carregue uma família de folha (carimbo) no projeto.");
                return Result.Cancelled;
            }

            var placed = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(v => v.ViewId));
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted && !(v is ViewSheet) &&
                            (v is ViewPlan || v is ViewDrafting || v is ViewSection || v is View3D || v is PanelScheduleView ||
                             (v is ViewSchedule s && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)))
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).ToList();

            List<CheckItem> items = views.Select(v => new CheckItem(
                $"[{Kind(v)}] {v.Name}" + (placed.Contains(v.Id) ? "  (já está em prancha)" : ""), v,
                v.Name.StartsWith("RE - ") && !placed.Contains(v.Id))).ToList();

            var window = new ChecklistWindow("Compor pranchas",
                    "Marque as vistas. Será criada uma prancha por vista (plantas e diagramas só podem estar em uma prancha).",
                    items, "Criar pranchas")
                .WithCombo("Folha:", titleBlocks.Select(t => new CheckItem($"{t.FamilyName} : {t.Name}", t)).ToList())
                .WithText("Prefixo da numeração:", "EL-");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var titleBlock = (FamilySymbol)window.ComboSelection.Tag;
            string prefix = window.Text ?? "";
            var existingNumbers = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber));
            int n = 1, created = 0;
            var notes = new List<string>();
            ViewSheet lastSheet = null;

            using (var t = new Transaction(doc, "Compor pranchas"))
            {
                t.Start();
                if (!titleBlock.IsActive) titleBlock.Activate();

                foreach (CheckItem item in window.CheckedItems)
                {
                    var view = (View)item.Tag;
                    try
                    {
                        ViewSheet sheet = ViewSheet.Create(doc, titleBlock.Id);
                        while (existingNumbers.Contains($"{prefix}{n:00}")) n++;
                        sheet.SheetNumber = $"{prefix}{n:00}";
                        existingNumbers.Add(sheet.SheetNumber);
                        sheet.Name = view.Name.StartsWith("RE - ") ? view.Name.Substring(5) : view.Name;
                        doc.Regenerate();

                        BoundingBoxUV outline = sheet.Outline;
                        var center = new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0);

                        if (view is PanelScheduleView psv)
                            PanelScheduleSheetInstance.Create(doc, psv.Id, sheet);
                        else if (view is ViewSchedule vs)
                            ScheduleSheetInstance.Create(doc, sheet.Id, vs.Id, center);
                        else if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                            Viewport.Create(doc, sheet.Id, view.Id, center);
                        else
                            notes.Add($"{view.Name}: já está em outra prancha — prancha criada vazia.");

                        lastSheet = sheet;
                        created++;
                    }
                    catch (Exception ex) { notes.Add($"{view.Name}: {ex.Message}"); }
                }
                t.Commit();
            }

            if (lastSheet != null) uidoc.ActiveView = lastSheet;
            TaskDialog.Show("Compor pranchas", $"{created} prancha(s) criada(s)." + (notes.Count > 0 ? "\n\n" + RevitHelpers.Join(notes) : ""));
            return Result.Succeeded;
        }

        private static string Kind(View v) => v switch
        {
            PanelScheduleView => "Quadro",
            ViewSchedule => "Tabela",
            ViewDrafting => "Desenho",
            ViewPlan => "Planta",
            ViewSection => "Corte",
            View3D => "3D",
            _ => v.ViewType.ToString(),
        };
    }
}
