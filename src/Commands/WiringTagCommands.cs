using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    internal static class WiringViews
    {
        public static bool Supported(View v) => v != null && !v.IsTemplate && (v is ViewPlan || v is ViewSection);

        public static View Active(ExternalCommandData data, string title)
        {
            View v = data.Application.ActiveUIDocument?.Document?.ActiveView;
            if (Supported(v)) return v;
            TaskDialog.Show(title, "Abra uma planta ou um corte.");
            return null;
        }

        /// <summary>Regera os textos de fiação das vistas indicadas e grava o estado.</summary>
        public static int Regenerate(Document doc, WiringAnnotations w, IEnumerable<View> views, string title)
        {
            int n = 0;
            using var t = new Transaction(doc, title);
            t.Start();
            WarningSwallower.Attach(t);
            foreach (View v in views) n += w.Generate(v);
            w.Save();
            t.Commit();
            return n;
        }

        /// <summary>Vistas que já têm textos de fiação do plugin.</summary>
        public static List<View> WithNotes(Document doc, WiringAnnotations w) =>
            w.Data.Views.Where(kv => kv.Value.Notes.Count > 0)
                .Select(kv => long.TryParse(kv.Key, out long id) ? doc.GetElement(new ElementId(id)) as View : null)
                .Where(Supported).ToList();
    }

    /// <summary>Cria (ou recria) os identificadores de fiação da vista ativa, como textos do plugin.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringTagsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Identificadores de fiação";
            View view = WiringViews.Active(data, title);
            if (view == null) return Result.Cancelled;
            Document doc = view.Document;
            var w = new WiringAnnotations(doc);
            int n = WiringViews.Regenerate(doc, w, new[] { view }, title);
            TaskDialog.Show(title, $"{n} identificador(es) de fiação na vista \"{view.Name}\" (visibilidade: {WiringAnnotations.ModeLabel(w.For(view).Mode)}).\n" +
                                   "Rode de novo depois de mudar circuitos ou o dimensionamento.");
            return Result.Succeeded;
        }
    }

    /// <summary>Afasta os identificadores de fiação sobrepostos na vista ativa.</summary>
    [Transaction(TransactionMode.Manual)]
    public class AdjustWiringTagsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Ajustar identificadores";
            View view = WiringViews.Active(data, title);
            if (view == null) return Result.Cancelled;
            Document doc = view.Document;
            var w = new WiringAnnotations(doc);
            if (w.For(view).Notes.Count == 0)
            {
                TaskDialog.Show(title, "Esta vista ainda não tem identificadores de fiação do plugin. Use Criar Identificadores de Fiação.");
                return Result.Cancelled;
            }
            int moved;
            using (var t = new Transaction(doc, title))
            {
                t.Start();
                moved = w.Adjust(view);
                t.Commit();
            }
            TaskDialog.Show(title, moved > 0 ? $"{moved} ajuste(s) feito(s) para separar textos sobrepostos." : "Nenhum texto sobreposto.");
            return Result.Succeeded;
        }
    }

    /// <summary>Visibilidade pré-definida: para cada vista, quais circuitos aparecem nos identificadores de fiação.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringVisibilityPresetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Visibilidade de identificadores — pré-definida";
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            var w = new WiringAnnotations(doc);
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(WiringViews.Supported)
                .OrderBy(v => (v as ViewPlan)?.GenLevel?.ProjectElevation ?? 0).ThenBy(v => v.Name).ToList();
            if (views.Count == 0) return Result.Cancelled;

            var modes = Enum.GetValues(typeof(WiringVisibility)).Cast<WiringVisibility>().ToList();
            var form = new FormWindow(title, "Escolha, para cada vista, os circuitos que aparecem nos identificadores de fiação. " +
                                             "As vistas que já têm identificadores são refeitas ao clicar em OK.", 720);
            var combos = new List<(View View, System.Windows.Controls.ComboBox Combo)>();
            foreach (View v in views)
            {
                string level = (v as ViewPlan)?.GenLevel?.Name;
                string label = (level != null ? $"[{level}] " : "") + v.Name;
                var combo = form.Combo(label, modes, WiringAnnotations.ModeLabel, modes.IndexOf(w.For(v).Mode));
                combos.Add((v, combo));
            }
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            var changed = new List<View>();
            foreach (var (v, combo) in combos)
            {
                WiringVisibility mode = FormWindow.Selected<WiringVisibility>(combo);
                if (w.For(v).Mode == mode) continue;
                w.For(v).Mode = mode;
                if (w.For(v).Notes.Count > 0) changed.Add(v);
            }
            int n = WiringViews.Regenerate(doc, w, changed, title);
            TaskDialog.Show(title, $"Visibilidade gravada. {changed.Count} vista(s) refeita(s), {n} identificador(es).");
            return Result.Succeeded;
        }
    }

    /// <summary>Visibilidade personalizada: escolher circuito a circuito o que aparece nos identificadores da vista ativa.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringVisibilityCustomCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Visibilidade de identificadores — personalizada";
            View view = WiringViews.Active(data, title);
            if (view == null) return Result.Cancelled;
            Document doc = view.Document;
            var w = new WiringAnnotations(doc);
            WiringAnnotations.ViewState s = w.For(view);

            var segIds = new HashSet<ElementId>(w.SegmentsIn(view).Select(e => e.Id));
            var inView = new HashSet<ElementId>(w.Routing.CircuitsBySegment.Where(kv => segIds.Contains(kv.Key)).SelectMany(kv => kv.Value));
            List<ElectricalSystem> circuits = w.AllCircuits.Where(c => inView.Contains(c.Id))
                .OrderBy(c => c.PanelName).ThenBy(c => c.CircuitNumber).ToList();
            if (circuits.Count == 0)
            {
                TaskDialog.Show(title, "Nenhum circuito roteado passa pela infraestrutura desta vista.");
                return Result.Cancelled;
            }

            var items = circuits.Select(c => new CheckItem($"{c.PanelName}-{c.CircuitNumber}  {c.LoadName}", c,
                s.Circuits == null || s.Circuits.Contains(c.Id.Value))).ToList();
            var window = new ChecklistWindow(title, "Marque os circuitos que aparecem nos identificadores de fiação desta vista.", items, "Aplicar")
                .AllowEmpty().WithOption1("Voltar a mostrar todos (pela visibilidade pré-definida)", false);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            s.Circuits = window.Option1Checked ? null : window.CheckedItems.Select(i => ((ElectricalSystem)i.Tag).Id.Value).ToList();
            int n = WiringViews.Regenerate(doc, w, new[] { view }, title);
            TaskDialog.Show(title, $"{n} identificador(es) refeito(s) na vista.");
            return Result.Succeeded;
        }
    }

    /// <summary>Cria os identificadores de fiação da vista ativa com a mesma configuração de outra vista do mesmo nível.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringTagsFromViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Criar identificadores baseado na vista";
            View view = WiringViews.Active(data, title);
            if (view is not ViewPlan plan) { if (view != null) TaskDialog.Show(title, "Abra uma planta."); return Result.Cancelled; }
            Document doc = view.Document;
            var w = new WiringAnnotations(doc);
            List<View> sources = w.Data.Views.Keys
                .Select(k => long.TryParse(k, out long id) ? doc.GetElement(new ElementId(id)) as ViewPlan : null)
                .Where(v => v != null && v.Id != view.Id && v.GenLevel?.Id == plan.GenLevel?.Id).Cast<View>().ToList();
            if (sources.Count == 0)
            {
                TaskDialog.Show(title, "Nenhuma outra vista deste nível tem identificadores de fiação configurados.");
                return Result.Cancelled;
            }
            var form = new FormWindow(title, "A vista ativa recebe a mesma visibilidade e os mesmos circuitos da vista escolhida.");
            var combo = form.Combo("Vista de referência:", sources, v => v.Name);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            WiringAnnotations.ViewState src = w.For(FormWindow.Selected<View>(combo)), dst = w.For(view);
            dst.Mode = src.Mode;
            dst.Circuits = src.Circuits?.ToList();
            int n = WiringViews.Regenerate(doc, w, new[] { view }, title);
            TaskDialog.Show(title, $"{n} identificador(es) criado(s).");
            return Result.Succeeded;
        }
    }

    /// <summary>Substituir por legenda: os trechos clicados mostram um número/letra no lugar da fiação.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringLegendSubstituteCommand : IExternalCommand
    {
        private class InfraFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Conduit || e is CableTray;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Substituir por legenda";
            View view = WiringViews.Active(data, title);
            if (view == null) return Result.Cancelled;
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = view.Document;
            IList<Reference> refs;
            try { refs = uidoc.Selection.PickObjects(ObjectType.Element, new InfraFilter(), "Selecione os trechos de eletroduto/eletrocalha e clique em Concluir"); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            var w = new WiringAnnotations(doc);
            foreach (Reference r in refs)
            {
                string uid = doc.GetElement(r)?.UniqueId;
                if (uid != null && !w.Data.LegendSegments.Contains(uid)) w.Data.LegendSegments.Add(uid);
            }
            int n = WiringViews.Regenerate(doc, w, new[] { view }, title);
            TaskDialog.Show(title, $"{refs.Count} trecho(s) passam a mostrar legenda. Use Criar Vista de Legenda para a tabela dos significados.");
            return Result.Succeeded;
        }
    }

    /// <summary>Auto substituir: trechos da vista com N circuitos ou mais passam a mostrar legenda.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringLegendAutoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Auto substituir por legenda";
            View view = WiringViews.Active(data, title);
            if (view == null) return Result.Cancelled;
            Document doc = view.Document;
            var form = new FormWindow(title, "Os trechos desta vista com a quantidade mínima de circuitos (ou mais) mostram uma legenda no lugar da fiação.");
            var min = form.Number("Quantidade mínima de circuitos no trecho:", 4, 1, 100);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            int limit = (int)FormWindow.NumberOf(min, 4);

            var w = new WiringAnnotations(doc);
            WiringAnnotations.ViewState s = w.For(view);
            int added = 0;
            foreach (Element seg in w.SegmentsIn(view))
            {
                if (w.CircuitsIn(seg.Id, s).Count < limit || w.Data.LegendSegments.Contains(seg.UniqueId)) continue;
                w.Data.LegendSegments.Add(seg.UniqueId);
                added++;
            }
            WiringViews.Regenerate(doc, w, new[] { view }, title);
            TaskDialog.Show(title, $"{added} trecho(s) substituído(s) por legenda.");
            return Result.Succeeded;
        }
    }

    /// <summary>Cria/atualiza a vista de desenho com o significado das legendas de fiação.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringLegendViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Criar vista de legenda";
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;
            var w = new WiringAnnotations(doc);
            if (w.Data.Legend.Count == 0)
            {
                TaskDialog.Show(title, "Ainda não há legendas. Use Substituir por Legenda ou Auto Substituir antes.");
                return Result.Cancelled;
            }
            View legend;
            using (var t = new Transaction(doc, title))
            {
                t.Start();
                legend = w.BuildLegendView();
                t.Commit();
            }
            if (legend != null) uidoc.ActiveView = legend;
            return Result.Succeeded;
        }
    }

    /// <summary>Reiniciar legendas: apaga a numeração e volta a mostrar a fiação nos trechos.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringLegendResetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            const string title = "Reiniciar legendas";
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            if (TaskDialog.Show(title, "Apagar toda a numeração das legendas de fiação e voltar a mostrar a fiação nos trechos?",
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No) != TaskDialogResult.Yes) return Result.Cancelled;
            var w = new WiringAnnotations(doc);
            w.Data.Legend.Clear();
            w.Data.LegendSegments.Clear();
            int n = WiringViews.Regenerate(doc, w, WiringViews.WithNotes(doc, w), title);
            TaskDialog.Show(title, $"Legendas reiniciadas. {n} identificador(es) refeito(s).");
            return Result.Succeeded;
        }
    }
}
