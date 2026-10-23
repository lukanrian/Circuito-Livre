using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    /// <summary>Base dos comandos de modificar com circuitos: seleção dos elementos e relatório.</summary>
    public abstract class ModifyWithCircuitsBase : IExternalCommand
    {
        protected abstract string Title { get; }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            try
            {
                List<ElementId> ids = uidoc.Selection.GetElementIds().ToList();
                if (ids.Count == 0)
                {
                    ids = uidoc.Selection.PickObjects(ObjectType.Element, $"{Title}: selecione dispositivos, quadros e identificadores e clique em Concluir")
                        .Select(r => r.ElementId).ToList();
                }
                ids = ids.Where(id => doc.GetElement(id) is not Autodesk.Revit.DB.Electrical.ElectricalSystem).ToList();
                if (ids.Count == 0) return Result.Cancelled;

                return Run(data, uidoc, ids);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
        }

        protected abstract Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids);

        protected void Report(int copies, CircuitCloner.Outcome outcome, string extra = null)
        {
            string text = $"{copies} elemento(s) criado(s); {outcome?.Created ?? 0} circuito(s) recriado(s).";
            if (!string.IsNullOrEmpty(extra)) text += "\n\n" + extra;
            if (outcome != null && outcome.Notes.Count > 0) text += "\n\n" + RevitHelpers.Join(outcome.Notes, 25);
            TaskDialog.Show(Title, text);
        }

        protected static XYZ Pick(UIDocument uidoc, string prompt) =>
            uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Intersections |
                                      ObjectSnapTypes.Nearest | ObjectSnapTypes.Perpendicular | ObjectSnapTypes.Centers, prompt);
    }

    /// <summary>Copia os elementos de um ponto para outro, recriando os circuitos nas cópias.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CopyWithCircuitsCommand : ModifyWithCircuitsBase
    {
        protected override string Title => "Copiar com circuitos";

        protected override Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids)
        {
            Document doc = uidoc.Document;
            XYZ from = Pick(uidoc, "Ponto de origem");
            XYZ to = Pick(uidoc, "Ponto de destino");
            XYZ move = new XYZ(to.X - from.X, to.Y - from.Y, 0);

            using var t = new Transaction(doc, Title);
            t.Start();
            ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, ids, move);
            doc.Regenerate();
            var map = CircuitCloner.MapCopies(doc, ids, copies, Transform.CreateTranslation(move));
            CircuitCloner.Outcome outcome = CircuitCloner.Recreate(doc, ids, map);
            t.Commit();

            Report(copies.Count, outcome);
            return Result.Succeeded;
        }
    }

    /// <summary>Copia os elementos para outros níveis (deslocamento vertical), recriando os circuitos.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CopyToLevelsCommand : ModifyWithCircuitsBase
    {
        protected override string Title => "Copiar para níveis";

        protected override Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids)
        {
            Document doc = uidoc.Document;
            List<ElementId> model = ids.Where(id => !(doc.GetElement(id)?.ViewSpecific ?? true)).ToList();
            int annotations = ids.Count - model.Count;
            if (model.Count == 0) return Result.Cancelled;

            List<Level> levels = RevitHelpers.Levels(doc);
            ElementId baseId = (doc.ActiveView as ViewPlan)?.GenLevel?.Id ?? levels.First().Id;
            var window = new ChecklistWindow(Title,
                    $"{model.Count} elemento(s) de modelo selecionado(s). Marque os níveis que devem receber a cópia.",
                    levels.Select(l => new CheckItem($"{l.Name}  ({RevitHelpers.ToM(l.ProjectElevation):N2} m)", l)).ToList(),
                    "Copiar")
                .WithCombo("Nível base dos elementos:", levels.Select(l => new CheckItem(l.Name, l)).ToList(),
                    Math.Max(0, levels.FindIndex(l => l.Id == baseId)));
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var baseLevel = (Level)window.ComboSelection.Tag;
            int total = 0;
            var outcome = new CircuitCloner.Outcome();

            using var t = new Transaction(doc, Title);
            t.Start();
            foreach (CheckItem item in window.CheckedItems)
            {
                var level = (Level)item.Tag;
                if (level.Id == baseLevel.Id) continue;
                var move = new XYZ(0, 0, level.ProjectElevation - baseLevel.ProjectElevation);
                ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, model, move);
                doc.Regenerate();
                total += copies.Count;
                var map = CircuitCloner.MapCopies(doc, model, copies, Transform.CreateTranslation(move));
                CircuitCloner.Outcome o = CircuitCloner.Recreate(doc, model, map);
                outcome.Created += o.Created;
                outcome.Notes.AddRange(o.Notes.Select(n => $"[{level.Name}] {n}"));
            }
            t.Commit();

            Report(total, outcome,
                (annotations > 0 ? $"{annotations} identificador(es)/anotação(ões) não foram copiados (são da vista) — use Criar Identificadores nas plantas de destino.\n" : "") +
                "As cópias ficam associadas ao nível base com deslocamento vertical.");
            return Result.Succeeded;
        }
    }

    /// <summary>Gira os elementos em torno de um ponto (centro, ângulo inicial e final). Os circuitos são mantidos.</summary>
    [Transaction(TransactionMode.Manual)]
    public class RotateWithCircuitsCommand : ModifyWithCircuitsBase
    {
        protected override string Title => "Rotacionar";

        protected override Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids)
        {
            Document doc = uidoc.Document;
            XYZ c = Pick(uidoc, "Centro da rotação");
            XYZ a = Pick(uidoc, "Ponto que define o ângulo inicial");
            XYZ b = Pick(uidoc, "Ponto que define o ângulo final");

            double angle = Math.Atan2(b.Y - c.Y, b.X - c.X) - Math.Atan2(a.Y - c.Y, a.X - c.X);
            Line axis = Line.CreateBound(c, c + XYZ.BasisZ);

            using var t = new Transaction(doc, Title);
            t.Start();
            ElementTransformUtils.RotateElements(doc, ids, axis, angle);
            t.Commit();

            TaskDialog.Show(Title, $"{ids.Count} elemento(s) girado(s) {angle * 180 / Math.PI:N1}°. Os circuitos foram mantidos.");
            return Result.Succeeded;
        }
    }

    /// <summary>Espelha (cópia) os elementos por um eixo definido por dois pontos, recriando os circuitos.</summary>
    [Transaction(TransactionMode.Manual)]
    public class MirrorByPointsCommand : ModifyWithCircuitsBase
    {
        protected override string Title => "Espelhar por pontos";

        protected override Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids)
        {
            XYZ a = Pick(uidoc, "Primeiro ponto do eixo de espelhamento");
            XYZ b = Pick(uidoc, "Segundo ponto do eixo de espelhamento");
            return MirrorHelper.Mirror(uidoc.Document, ids, a, b, Title, Report);
        }
    }

    /// <summary>Espelha (cópia) os elementos por uma linha existente (linha de detalhe/modelo, parede, grade).</summary>
    [Transaction(TransactionMode.Manual)]
    public class MirrorByLineCommand : ModifyWithCircuitsBase
    {
        protected override string Title => "Espelhar por linha";

        protected override Result Run(ExternalCommandData data, UIDocument uidoc, List<ElementId> ids)
        {
            Document doc = uidoc.Document;
            Reference r = uidoc.Selection.PickObject(ObjectType.Element, new CurveFilter(), "Selecione a linha do eixo de espelhamento");
            Element e = doc.GetElement(r);
            Curve curve = e is Grid g ? g.Curve : (e.Location as LocationCurve)?.Curve ?? (e as CurveElement)?.GeometryCurve;
            if (curve == null) return Result.Cancelled;
            return MirrorHelper.Mirror(doc, ids, curve.GetEndPoint(0), curve.GetEndPoint(1), Title, Report);
        }

        private class CurveFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is CurveElement || e is Grid || e.Location is LocationCurve;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }

    internal static class MirrorHelper
    {
        public static Result Mirror(Document doc, List<ElementId> ids, XYZ a, XYZ b, string title,
            Action<int, CircuitCloner.Outcome, string> report)
        {
            XYZ dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (dir.GetLength() < 1e-6) return Result.Cancelled;
            XYZ normal = dir.Normalize().CrossProduct(XYZ.BasisZ);
            Plane plane = Plane.CreateByNormalAndOrigin(normal, new XYZ(a.X, a.Y, 0));

            using var t = new Transaction(doc, title);
            t.Start();
            var before = new HashSet<ElementId>(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds());
            ElementTransformUtils.MirrorElements(doc, ids, plane, true);
            doc.Regenerate();
            List<ElementId> copies = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()
                .Where(id => !before.Contains(id)).ToList();
            var map = CircuitCloner.MapCopies(doc, ids, copies, Transform.CreateReflection(plane));
            CircuitCloner.Outcome outcome = CircuitCloner.Recreate(doc, ids, map);
            t.Commit();

            report(copies.Count, outcome, null);
            return Result.Succeeded;
        }
    }
}
