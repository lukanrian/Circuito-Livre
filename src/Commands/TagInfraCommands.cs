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
    internal class TagChoice
    {
        public ElementId Id;
        public string Label;

        public static List<TagChoice> Of(Document doc, BuiltInCategory category, string none)
        {
            var list = new List<TagChoice> { new TagChoice { Id = null, Label = none } };
            list.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.BuiltInCategory == category)
                .OrderBy(s => s.FamilyName + s.Name)
                .Select(s => new TagChoice { Id = s.Id, Label = $"{s.FamilyName} : {s.Name}" }));
            return list;
        }
    }

    /// <summary>
    /// Identificadores de dimensão: na vista ativa, marca os eletrodutos/eletrocalhas com tamanho diferente do padrão
    /// (o padrão não leva identificador, para limpar a planta).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class TagDimensionCommand : IExternalCommand
    {
        private const string Title = "Identificadores de dimensão";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            View view = doc?.ActiveView;
            if (view is not ViewPlan && view is not ViewSection)
            {
                TaskDialog.Show(Title, "Abra uma planta ou um corte.");
                return Result.Cancelled;
            }

            List<TagChoice> conduitTags = TagChoice.Of(doc, BuiltInCategory.OST_ConduitTags, "(não marcar eletrodutos)");
            List<TagChoice> trayTags = TagChoice.Of(doc, BuiltInCategory.OST_CableTrayTags, "(não marcar eletrocalhas)");
            var form = new FormWindow(Title, "Marca na vista ativa só os trechos com tamanho diferente do padrão. " +
                                             "O identificador deve mostrar o diâmetro (eletroduto) ou a largura × altura (eletrocalha).");
            DimensioningSettings cfg = DimensioningSettings.Load();
            int Preferred(List<TagChoice> tags, string category) => cfg.TagByCategory.TryGetValue(category, out string label)
                ? Math.Max(0, tags.FindIndex(x => x.Label == label)) : tags.Count > 1 ? 1 : 0;
            var conduitCombo = form.Combo("Identificador de eletroduto:", conduitTags, c => c.Label, Preferred(conduitTags, "Eletrodutos"));
            var diameter = form.Number("Diâmetro padrão (mm) — não marcar:", cfg.TagIgnoreConduitDiameterMm, 0, 500);
            form.Note("Tipos com diâmetro padrão próprio (Configurações → Conduítes) usam o valor de lá.");
            var trayCombo = form.Combo("Identificador de eletrocalha:", trayTags, c => c.Label, Preferred(trayTags, "Eletrocalhas"));
            var width = form.Number("Largura padrão (mm):", cfg.TagIgnoreTrayWidthMm, 0, 2000);
            var height = form.Number("Altura padrão (mm):", cfg.TagIgnoreTrayHeightMm, 0, 1000);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            ElementId conduitTag = FormWindow.Selected<TagChoice>(conduitCombo)?.Id;
            ElementId trayTag = FormWindow.Selected<TagChoice>(trayCombo)?.Id;
            double dStd = FormWindow.NumberOf(diameter), wStd = FormWindow.NumberOf(width), hStd = FormWindow.NumberOf(height);

            var already = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>().SelectMany(t => t.GetTaggedLocalElementIds()));

            int tagged = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                if (conduitTag != null)
                {
                    foreach (Conduit c in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Conduit)).Cast<Conduit>())
                    {
                        string typeName = doc.GetElement(c.GetTypeId())?.Name;
                        double std = cfg.ConduitDefaultDiameter.TryGetValue(typeName ?? "", out double own) ? own : dStd;
                        if (already.Contains(c.Id) || Math.Abs(RevitHelpers.ToM(c.Diameter) * 1000 - std) < 0.5) continue;
                        if (Tag(doc, view, c, conduitTag)) tagged++; else fail++;
                    }
                }
                if (trayTag != null)
                {
                    foreach (CableTray c in new FilteredElementCollector(doc, view.Id).OfClass(typeof(CableTray)).Cast<CableTray>())
                    {
                        if (already.Contains(c.Id)) continue;
                        bool standard = Math.Abs(RevitHelpers.ToM(c.Width) * 1000 - wStd) < 0.5 && Math.Abs(RevitHelpers.ToM(c.Height) * 1000 - hStd) < 0.5;
                        if (standard) continue;
                        if (Tag(doc, view, c, trayTag)) tagged++; else fail++;
                    }
                }
                t.Commit();
            }

            TaskDialog.Show(Title, $"{tagged} identificador(es) criado(s)." + (fail > 0 ? $"\n{fail} não puderam ser criados." : ""));
            return Result.Succeeded;
        }

        internal static bool Tag(Document doc, View view, Element e, ElementId tagType)
        {
            try
            {
                XYZ p = RevitHelpers.PointOf(e);
                if (p == null) return false;
                IndependentTag.Create(doc, tagType, view.Id, new Reference(e), false, TagOrientation.Horizontal, p);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Identificadores de infra de nível: na planta ativa, trechos verticais que sobem para o nível de cima, descem
    /// para o de baixo ou passam pelo pavimento recebem SOBE / DESCE / PASSA em RE_Passagem e um identificador (ou texto).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class TagLevelInfraCommand : IExternalCommand
    {
        private const string Title = "Identificadores de infra de nível";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan view || view.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra a planta de um pavimento.");
                return Result.Cancelled;
            }

            List<TagChoice> tags = TagChoice.Of(doc, BuiltInCategory.OST_ConduitTags, "(usar texto: SOBE / DESCE / PASSA)");
            var form = new FormWindow(Title, "Trechos verticais de eletroduto/eletrocalha que saem deste pavimento recebem SOBE, DESCE ou PASSA " +
                                             "no parâmetro RE_Passagem. Para usar um identificador, ele deve mostrar RE_Passagem.");
            var combo = form.Combo("Identificador:", tags, c => c.Label);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            ElementId tagType = FormWindow.Selected<TagChoice>(combo)?.Id;

            List<Level> levels = RevitHelpers.Levels(doc);
            Level level = view.GenLevel;
            DimensioningSettings cfg = DimensioningSettings.Load();
            if (!cfg.ConsiderInfraLevel(level.Name))
            {
                TaskDialog.Show(Title, $"O nível {level.Name} está desmarcado em Configurações → Cota do conduíte.");
                return Result.Cancelled;
            }
            double floor = level.ProjectElevation + RevitHelpers.M(cfg.FloorOffset(level.Name));
            double above = levels.Where(l => cfg.ConsiderInfraLevel(l.Name) && l.ProjectElevation > level.ProjectElevation + RevitHelpers.M(0.5))
                .Select(l => l.ProjectElevation + RevitHelpers.M(cfg.FloorOffset(l.Name)))
                .DefaultIfEmpty(floor + RevitHelpers.M(3.0)).Min();
            double tol = RevitHelpers.M(0.30);

            var marks = new List<(Element E, string Text)>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                         .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray }))
                         .WhereElementIsNotElementType())
            {
                if ((e.Location as LocationCurve)?.Curve is not Line line || Math.Abs(line.Direction.Z) < 0.7) continue;
                double z0 = Math.Min(line.GetEndPoint(0).Z, line.GetEndPoint(1).Z), z1 = Math.Max(line.GetEndPoint(0).Z, line.GetEndPoint(1).Z);
                bool up = z1 >= above - tol, down = z0 <= floor - tol;
                if (!up && !down) continue;
                marks.Add((e, up && down ? "PASSA" : up ? "SOBE" : "DESCE"));
            }
            if (marks.Count == 0)
            {
                TaskDialog.Show(Title, "Nenhum trecho vertical saindo deste pavimento na vista ativa.");
                return Result.Cancelled;
            }

            int done = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                SharedParameters.Ensure(doc, SharedParameters.LevelPass);
                doc.Regenerate();
                ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var (e, text) in marks)
                {
                    SharedParameters.SetText(e, SharedParameters.LevelPass, text);
                    bool ok = tagType != null
                        ? TagDimensionCommand.Tag(doc, view, e, tagType)
                        : CreateText(doc, view, e, text, textType);
                    if (ok) done++;
                }
                t.Commit();
            }

            TaskDialog.Show(Title, $"{done} de {marks.Count} trecho(s) identificado(s) ({string.Join(", ", marks.GroupBy(m => m.Text).Select(g => $"{g.Count()} {g.Key}"))}).");
            return Result.Succeeded;
        }

        private static bool CreateText(Document doc, View view, Element e, string text, ElementId textType)
        {
            try
            {
                XYZ p = RevitHelpers.PointOf(e);
                if (p == null) return false;
                XYZ at = new XYZ(p.X, p.Y, 0) + view.RightDirection * RevitHelpers.M(0.15) * view.Scale / 50.0;
                TextNote.Create(doc, view.Id, at, text, textType);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
