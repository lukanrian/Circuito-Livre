using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Legenda de pontos: dá um número a cada tipo de ponto (quadro, tomada, luminária, interruptor, telecom),
    /// grava o número em RE_Legenda em todas as instâncias, monta a vista "RE - Legenda de pontos" e, se pedido,
    /// coloca identificadores na vista ativa. Os números ficam guardados no projeto e não mudam entre execuções.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LegendCommand : IExternalCommand
    {
        public const string ViewName = "RE - Legenda de pontos";
        private const string StorageKey = "legenda";

        protected virtual bool Reset => false;
        private string Title => Reset ? "Reiniciar legenda" : "Legenda de pontos";

        private class TagOption
        {
            public ElementId Id;
            public string Label;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) return Result.Cancelled;
            View view = doc.ActiveView;
            bool graphicView = view is ViewPlan || view is ViewSection || view is View3D;

            var tagOptions = new List<TagOption> { new TagOption { Id = null, Label = "(não criar identificadores)" } };
            tagOptions.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.BuiltInCategory == BuiltInCategory.OST_MultiCategoryTags)
                .OrderBy(s => s.FamilyName + s.Name)
                .Select(s => new TagOption { Id = s.Id, Label = $"{s.FamilyName} : {s.Name}" }));

            var form = new FormWindow(Title, Reset
                ? "Apaga a numeração atual e numera de novo, a partir de 1, na ordem: quadros, tomadas, luminárias, interruptores, telecom."
                : "Numera cada tipo de ponto, grava o número em RE_Legenda e monta a vista \"" + ViewName + "\". " +
                  "Tipos que já têm número mantêm o número; tipos novos recebem os próximos.");
            form.Group("Tipos da legenda");
            var onlyView = form.Radio("scope", "Somente os tipos que aparecem na vista ativa", graphicView);
            form.Radio("scope", "Todos os tipos usados no projeto", !graphicView);
            form.Group("Identificadores");
            var tagCombo = form.Combo("Identificador na vista ativa:", tagOptions, o => o.Label);
            form.Note("Use um identificador multicategoria que mostre o parâmetro RE_Legenda (o número fica dentro do símbolo).");
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            bool scopeView = onlyView.IsChecked == true && graphicView;
            TagOption tag = FormWindow.Selected<TagOption>(tagCombo);
            var categoryFilter = new ElementMulticategoryFilter(SharedParameters.LegendCategories);

            List<FamilyInstance> all = new FilteredElementCollector(doc).WherePasses(categoryFilter)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(f => f.Symbol != null).ToList();
            List<FamilyInstance> inView = graphicView
                ? new FilteredElementCollector(doc, view.Id).WherePasses(categoryFilter)
                    .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(f => f.Symbol != null).ToList()
                : new List<FamilyInstance>();
            List<FamilyInstance> scope = scopeView ? inView : all;
            if (scope.Count == 0)
            {
                TaskDialog.Show(Title, scopeView ? "Nenhum ponto elétrico ou de telecom visível na vista ativa." : "Nenhum ponto no projeto.");
                return Result.Cancelled;
            }

            // tipos na ordem da legenda
            List<FamilySymbol> types = scope.Select(f => f.Symbol).GroupBy(s => s.Id).Select(g => g.First())
                .OrderBy(s => Array.IndexOf(SharedParameters.LegendCategories, s.Category.BuiltInCategory))
                .ThenBy(s => s.FamilyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Dictionary<string, int> map = Reset ? new Dictionary<string, int>() : ReadMap(doc);
            int next = map.Values.DefaultIfEmpty(0).Max() + 1;
            foreach (FamilySymbol s in types)
                if (!map.ContainsKey(s.UniqueId)) map[s.UniqueId] = next++;

            int written = 0, tagged = 0, tagFail = 0;
            View legend;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                SharedParameters.Ensure(doc, SharedParameters.Legend);
                doc.Regenerate();
                ProjectStorage.SetExtra(doc, StorageKey, JsonSerializer.Serialize(map));

                foreach (FamilyInstance f in all)
                {
                    string value = map.TryGetValue(f.Symbol.UniqueId, out int n) ? n.ToString() : string.Empty;
                    if (SharedParameters.GetText(f, SharedParameters.Legend) == value) continue;
                    if (SharedParameters.SetText(f, SharedParameters.Legend, value)) written++;
                }

                legend = BuildLegendView(doc, types.OrderBy(s => map[s.UniqueId]).ToList(), map, scope);

                if (tag?.Id != null && graphicView)
                {
                    var already = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>().Where(x => x.GetTypeId() == tag.Id).SelectMany(x => x.GetTaggedLocalElementIds()));
                    XYZ up = view.UpDirection * RevitHelpers.M(0.25);
                    foreach (FamilyInstance f in inView.Where(f => !already.Contains(f.Id)))
                    {
                        XYZ p = RevitHelpers.PointOf(f);
                        if (p == null) continue;
                        try
                        {
                            IndependentTag.Create(doc, tag.Id, view.Id, new Reference(f), false, TagOrientation.Horizontal, p + up);
                            tagged++;
                        }
                        catch (Exception) { tagFail++; }
                    }
                }
                t.Commit();
            }

            TaskDialog.Show(Title,
                $"{types.Count} tipo(s) na legenda; RE_Legenda atualizado em {written} ponto(s)." +
                (legend != null ? $"\nVista: \"{legend.Name}\" (no Navegador de projeto)." : "\nNão foi possível criar a vista de legenda.") +
                (tagged > 0 ? $"\n{tagged} identificador(es) criado(s) na vista ativa." : "") +
                (tagFail > 0 ? $"\n{tagFail} identificador(es) não puderam ser criados." : ""));
            return Result.Succeeded;
        }

        private static Dictionary<string, int> ReadMap(Document doc)
        {
            string json = ProjectStorage.GetExtra(doc, StorageKey);
            if (json == null) return new Dictionary<string, int>();
            try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>(); }
            catch (Exception) { return new Dictionary<string, int>(); }
        }

        /// <summary>Cria ou limpa a vista de legenda e escreve as linhas (número, família : tipo, descrição, quantidade).</summary>
        private static View BuildLegendView(Document doc, List<FamilySymbol> types, Dictionary<string, int> map, List<FamilyInstance> scope)
        {
            View legend = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName &&
                                     (v.ViewType == ViewType.Legend || v.ViewType == ViewType.DraftingView));
            try
            {
                if (legend == null)
                {
                    // uma legenda de verdade pode ir em várias pranchas: duplica uma legenda existente, se houver
                    View source = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Legend);
                    if (source != null)
                    {
                        try { legend = doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as View; }
                        catch (Exception) { legend = null; }
                    }
                    if (legend == null)
                    {
                        ViewFamilyType drafting = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
                        if (drafting == null) return null;
                        legend = ViewDrafting.Create(doc, drafting.Id);
                    }
                    try { legend.Name = ViewName; } catch (Exception) { }
                }

                List<ElementId> owned = new FilteredElementCollector(doc, legend.Id).WhereElementIsNotElementType()
                    .Where(e => e.OwnerViewId == legend.Id).Select(e => e.Id).ToList();
                foreach (ElementId id in owned)
                {
                    try { doc.Delete(id); } catch (Exception) { }
                }

                ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                double scale = Math.Max(1, legend.Scale);
                double Mm(double mm) => mm / 304.8 * scale;

                TextNote.Create(doc, legend.Id, new XYZ(0, 0, 0), "LEGENDA", textType);
                Dictionary<ElementId, int> counts = scope.GroupBy(f => f.Symbol.Id).ToDictionary(g => g.Key, g => g.Count());
                double y = -Mm(10);
                foreach (FamilySymbol s in types)
                {
                    string description = s.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString();
                    if (string.IsNullOrWhiteSpace(description)) description = s.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString();
                    string text = $"{s.FamilyName} : {s.Name}" +
                                  (string.IsNullOrWhiteSpace(description) ? "" : $" — {description}") +
                                  (counts.TryGetValue(s.Id, out int c) ? $"  ({c} un.)" : "");
                    TextNote.Create(doc, legend.Id, new XYZ(0, y, 0), map[s.UniqueId].ToString(), textType);
                    TextNote.Create(doc, legend.Id, new XYZ(Mm(12), y, 0), text, textType);
                    y -= Mm(7);
                }
                return legend;
            }
            catch (Exception) { return legend; }
        }
    }

    /// <summary>Apaga a numeração da legenda e numera de novo a partir de 1.</summary>
    [Transaction(TransactionMode.Manual)]
    public class LegendResetCommand : LegendCommand
    {
        protected override bool Reset => true;
    }
}
