using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    internal static class LinkPicker
    {
        public static RevitLinkInstance Pick(ExternalCommandData data, Document doc, string title, string instruction)
        {
            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            if (links.Count == 0)
            {
                TaskDialog.Show(title, "Nenhum vínculo do Revit carregado neste projeto.");
                return null;
            }
            var form = new FormWindow(title, instruction);
            var combo = form.Combo("Vínculo:", links, l => l.Name);
            RevitHelpers.ShowOwned(form, data);
            return form.ShowDialog() == true ? FormWindow.Selected<RevitLinkInstance>(combo) : null;
        }
    }

    /// <summary>
    /// Importar diagramas: copia as vistas de desenho (diagramas unifilares, detalhes) de um modelo vinculado para
    /// vistas de desenho novas neste projeto.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportDiagramsCommand : IExternalCommand
    {
        private const string Title = "Importar diagramas";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            RevitLinkInstance link = LinkPicker.Pick(data, doc, Title, "Escolha o modelo vinculado de onde vêm os diagramas.");
            if (link == null) return Result.Cancelled;
            Document linkDoc = link.GetLinkDocument();

            List<ViewDrafting> views = new FilteredElementCollector(linkDoc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .Where(v => !v.IsTemplate).OrderBy(v => v.Name).ToList();
            if (views.Count == 0) { TaskDialog.Show(Title, "O vínculo não tem vistas de desenho."); return Result.Cancelled; }

            var items = views.Select(v => new CheckItem(v.Name, v,
                v.Name.IndexOf("unifilar", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("diagrama", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            var window = new ChecklistWindow(Title, "Marque as vistas de desenho do vínculo a importar.", items, "Importar");
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            ViewFamilyType drafting = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
            if (drafting == null) return Result.Cancelled;

            string prefix = link.Name.Split(':')[0].Trim();
            int ok = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                foreach (ViewDrafting source in window.CheckedItems.Select(i => (ViewDrafting)i.Tag))
                {
                    try
                    {
                        ViewDrafting target = ViewDrafting.Create(doc, drafting.Id);
                        target.Scale = source.Scale;
                        try { target.Name = $"{source.Name} ({prefix})"; } catch (Exception) { }
                        List<ElementId> ids = new FilteredElementCollector(linkDoc, source.Id).WhereElementIsNotElementType()
                            .Where(e => e.OwnerViewId == source.Id).Select(e => e.Id).ToList();
                        if (ids.Count > 0)
                            ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, new CopyPasteOptions());
                        ok++;
                    }
                    catch (Exception) { fail++; }
                }
                t.Commit();
            }
            TaskDialog.Show(Title, $"{ok} diagrama(s) importado(s)." + (fail > 0 ? $"\n{fail} não puderam ser copiados." : ""));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Importar identificadores: recria na vista ativa os identificadores de uma planta do modelo vinculado, apontando
    /// para os elementos do vínculo (os tipos de identificador são copiados se não existirem aqui).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportTagsCommand : IExternalCommand
    {
        private const string Title = "Importar identificadores";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan target)
            {
                TaskDialog.Show(Title, "Abra a planta que vai receber os identificadores.");
                return Result.Cancelled;
            }
            RevitLinkInstance link = LinkPicker.Pick(data, doc, Title, "Escolha o modelo vinculado já documentado.");
            if (link == null) return Result.Cancelled;
            Document linkDoc = link.GetLinkDocument();
            Transform tr = link.GetTotalTransform();

            List<ViewPlan> plans = new FilteredElementCollector(linkDoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && new FilteredElementCollector(linkDoc, v.Id).OfClass(typeof(IndependentTag)).Any())
                .OrderBy(v => v.Name).ToList();
            if (plans.Count == 0) { TaskDialog.Show(Title, "Nenhuma planta do vínculo tem identificadores."); return Result.Cancelled; }

            var form = new FormWindow(Title, $"Os identificadores da planta escolhida serão recriados na vista \"{target.Name}\".");
            var combo = form.Combo("Planta do vínculo:", plans, v => v.Name);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            ViewPlan source = FormWindow.Selected<ViewPlan>(combo);

            List<IndependentTag> tags = new FilteredElementCollector(linkDoc, source.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToList();
            int ok = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                var typeMap = new Dictionary<ElementId, ElementId>();
                foreach (IndependentTag tag in tags)
                {
                    try
                    {
                        ElementId hostType = HostType(doc, linkDoc, tag.GetTypeId(), typeMap);
                        if (hostType == null) { fail++; continue; }
                        foreach (ElementId taggedId in tag.GetTaggedLocalElementIds())
                        {
                            Element linked = linkDoc.GetElement(taggedId);
                            if (linked == null) continue;
                            Reference r = new Reference(linked).CreateLinkReference(link);
                            XYZ head = tr.OfPoint(tag.TagHeadPosition);
                            IndependentTag.Create(doc, hostType, target.Id, r, tag.HasLeader, tag.TagOrientation, head);
                            ok++;
                        }
                    }
                    catch (Exception) { fail++; }
                }
                t.Commit();
            }
            TaskDialog.Show(Title, $"{ok} identificador(es) importado(s)." + (fail > 0 ? $"\n{fail} não puderam ser recriados." : ""));
            return Result.Succeeded;
        }

        /// <summary>Tipo de identificador equivalente neste projeto (pelo nome) ou copiado do vínculo.</summary>
        private static ElementId HostType(Document doc, Document linkDoc, ElementId linkType, Dictionary<ElementId, ElementId> cache)
        {
            if (cache.TryGetValue(linkType, out ElementId id)) return id;
            var source = linkDoc.GetElement(linkType) as FamilySymbol;
            if (source == null) return null;
            FamilySymbol existing = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == source.FamilyName && s.Name == source.Name);
            if (existing == null)
            {
                try
                {
                    ICollection<ElementId> copied = ElementTransformUtils.CopyElements(linkDoc, new List<ElementId> { linkType }, doc, Transform.Identity, new CopyPasteOptions());
                    existing = copied.Select(c => doc.GetElement(c)).OfType<FamilySymbol>().FirstOrDefault();
                }
                catch (Exception) { }
            }
            cache[linkType] = existing?.Id;
            return existing?.Id;
        }
    }
}
