using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Services;
using CircuitoLivre.Views;

namespace CircuitoLivre.Commands
{
    internal static class Placement
    {
        public static List<FamilySymbol> Symbols(Document doc, BuiltInCategory category) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.BuiltInCategory == category)
                .OrderBy(s => s.FamilyName + " " + s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static void EnsureSketchPlane(Document doc, ViewPlan plan)
        {
            if (plan.SketchPlane != null || plan.GenLevel == null) return;
            using var t = new Transaction(doc, "Plano de trabalho");
            t.Start();
            plan.SketchPlane = SketchPlane.Create(doc, plan.GenLevel.Id);
            t.Commit();
        }
    }

    /// <summary>
    /// Adicionar luminárias em malha: dois cliques definem um retângulo, dividido em linhas × colunas; uma luminária
    /// no centro de cada célula, com rotação opcional da malha.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LightingGridCommand : IExternalCommand
    {
        private const string Title = "Adicionar luminárias";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan plan || plan.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra a planta do pavimento.");
                return Result.Cancelled;
            }
            List<FamilySymbol> symbols = Placement.Symbols(doc, BuiltInCategory.OST_LightingFixtures);
            if (symbols.Count == 0)
            {
                TaskDialog.Show(Title, "Carregue uma família de luminária no projeto.");
                return Result.Cancelled;
            }

            var form = new FormWindow(Title, "Depois de OK, clique em dois cantos opostos do retângulo. As luminárias ficam no centro de cada célula da malha.");
            var symbolCombo = form.Combo("Luminária:", symbols, s => $"{s.FamilyName} : {s.Name}");
            var rows = form.Number("Linhas:", 2, 1, 50);
            var cols = form.Number("Colunas:", 2, 1, 50);
            var height = form.Number("Altura (m) — 0 mantém a da família:", 0, 0, 20);
            var angle = form.Number("Rotação da malha (graus):", 0, -360, 360);
            var power = form.Number("Potência (VA) — 0 mantém a da família:", 0, 0, 100000);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            var symbol = FormWindow.Selected<FamilySymbol>(symbolCombo);
            int nr = (int)FormWindow.NumberOf(rows, 1), nc = (int)FormWindow.NumberOf(cols, 1);
            double h = FormWindow.NumberOf(height), va = FormWindow.NumberOf(power);
            double theta = FormWindow.NumberOf(angle) * Math.PI / 180;

            XYZ a, b;
            try
            {
                Placement.EnsureSketchPlane(doc, plan);
                a = uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections, "Clique no primeiro canto do retângulo");
                b = uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections, "Clique no canto oposto");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            var u = new XYZ(Math.Cos(theta), Math.Sin(theta), 0);
            var v = new XYZ(-Math.Sin(theta), Math.Cos(theta), 0);
            XYZ diag = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            double w = diag.DotProduct(u), hh = diag.DotProduct(v);
            Level level = plan.GenLevel;

            var placed = new List<ElementId>();
            var notes = new HashSet<string>();
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                for (int i = 0; i < nr; i++)
                {
                    for (int j = 0; j < nc; j++)
                    {
                        XYZ p = new XYZ(a.X, a.Y, level.ProjectElevation) + u * (w * (j + 0.5) / nc) + v * (hh * (i + 0.5) / nr);
                        try
                        {
                            FamilyInstance fi = doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
                            if (h > 0) LaunchPointsCommand.SetHeight(fi, h);
                            if (va > 0) LaunchPointsCommand.SetPower(fi, va);
                            if (Math.Abs(theta) > 1e-6)
                                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), theta);
                            placed.Add(fi.Id);
                        }
                        catch (Exception ex) { notes.Add(ex.Message); }
                    }
                }
                t.Commit();
            }

            TaskDialog.Show(Title, $"{placed.Count} luminária(s) lançada(s)." +
                (notes.Count > 0 ? "\n\nAlgumas não puderam ser inseridas (família baseada em face/teto? use Lançar Pontos):\n" + string.Join("\n", notes.Take(3)) : ""));
            if (placed.Count > 0) uidoc.Selection.SetElementIds(placed);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Adicionar interruptores: clique na porta (do modelo ou do vínculo) e no lado onde vai o interruptor.
    /// O interruptor fica na parede da porta, a uma distância do batente, na altura escolhida.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SwitchByDoorCommand : IExternalCommand
    {
        private const string Title = "Adicionar interruptores";

        private class DoorFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public DoorFilter(Document doc) => _doc = doc;
            public bool AllowElement(Element e) =>
                e.Category?.BuiltInCategory == BuiltInCategory.OST_Doors || e is RevitLinkInstance;
            public bool AllowReference(Reference r, XYZ p)
            {
                if (_doc.GetElement(r) is not RevitLinkInstance link) return true;
                Element linked = link.GetLinkDocument()?.GetElement(r.LinkedElementId);
                return linked?.Category?.BuiltInCategory == BuiltInCategory.OST_Doors;
            }
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan plan || plan.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra a planta do pavimento.");
                return Result.Cancelled;
            }
            List<FamilySymbol> symbols = Placement.Symbols(doc, BuiltInCategory.OST_LightingDevices);
            if (symbols.Count == 0)
            {
                TaskDialog.Show(Title, "Carregue uma família de interruptor (Dispositivos de iluminação) no projeto.");
                return Result.Cancelled;
            }

            var form = new FormWindow(Title, "Depois de OK: clique na porta e depois no lado (ambiente e lado do batente) onde vai o interruptor. ESC termina.");
            var symbolCombo = form.Combo("Interruptor:", symbols, s => $"{s.FamilyName} : {s.Name}");
            var height = form.Number("Altura (m):", 1.10, 0, 5);
            var gap = form.Number("Distância do batente (m):", 0.10, 0, 3);
            var linked = form.Radio("src", "Porta do vínculo da arquitetura", true);
            form.Radio("src", "Porta deste modelo", false);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            var symbol = FormWindow.Selected<FamilySymbol>(symbolCombo);
            double h = FormWindow.NumberOf(height, 1.1), dist = RevitHelpers.M(FormWindow.NumberOf(gap, 0.1));
            Level level = plan.GenLevel;
            var notes = new HashSet<string>();
            var placed = new List<ElementId>();
            Placement.EnsureSketchPlane(doc, plan);

            while (true)
            {
                FamilyInstance door;
                RevitLinkInstance link = null;
                XYZ side;
                try
                {
                    Reference r = uidoc.Selection.PickObject(linked.IsChecked == true ? ObjectType.LinkedElement : ObjectType.Element,
                        new DoorFilter(doc), "Clique na porta (ESC termina)");
                    if (linked.IsChecked == true)
                    {
                        link = doc.GetElement(r) as RevitLinkInstance;
                        door = link?.GetLinkDocument()?.GetElement(r.LinkedElementId) as FamilyInstance;
                    }
                    else door = doc.GetElement(r) as FamilyInstance;
                    if (door?.Host is not Wall) { TaskDialog.Show(Title, "A porta precisa estar numa parede."); continue; }
                    side = uidoc.Selection.PickPoint("Clique no lado onde vai o interruptor (dentro do ambiente, perto do batente)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                Transform tr = link?.GetTotalTransform() ?? Transform.Identity;
                var wall = (Wall)door.Host;
                if ((wall.Location as LocationCurve)?.Curve is not Line wallLine || door.Location is not LocationPoint lp)
                {
                    notes.Add("Parede curva ou porta sem ponto de inserção: não suportado.");
                    continue;
                }

                XYZ c = tr.Inverse.OfPoint(side);
                XYZ d = new XYZ(wallLine.Direction.X, wallLine.Direction.Y, 0).Normalize();
                XYZ n = XYZ.BasisZ.CrossProduct(d);
                XYZ rel = new XYZ(c.X - lp.Point.X, c.Y - lp.Point.Y, 0);
                double along = rel.DotProduct(d) >= 0 ? 1 : -1;
                XYZ inward = rel.DotProduct(n) >= 0 ? n : n.Negate();
                double floor = door.LevelId != ElementId.InvalidElementId && door.Document.GetElement(door.LevelId) is Level dl
                    ? dl.ProjectElevation : lp.Point.Z;
                XYZ onWall = new XYZ(lp.Point.X, lp.Point.Y, floor + RevitHelpers.M(h))
                             + d * along * (WallPlacement.DoorWidth(door) / 2 + dist)
                             + inward * (wall.Width / 2);

                using var t = new Transaction(doc, Title);
                t.Start();
                WarningSwallower.Attach(t);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                FamilyInstance fi = WallPlacement.Place(doc, wall, link, tr, onWall, d, inward, symbol, level, h, notes);
                if (fi != null) placed.Add(fi.Id);
                t.Commit();
            }

            if (placed.Count > 0 || notes.Count > 0)
                TaskDialog.Show(Title, $"{placed.Count} interruptor(es) lançado(s)." + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""));
            return placed.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }
    }
}
