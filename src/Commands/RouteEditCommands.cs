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
    /// <summary>Filtro: eletroduto ou eletrocalha retos (também ao clicar num ponto sobre eles).</summary>
    internal class StraightRunFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) =>
            (e is Conduit || e is CableTray) && (e.Location as LocationCurve)?.Curve is Line;
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    /// <summary>
    /// Desvio de infraestrutura: entre dois pontos clicados num eletroduto/eletrocalha reto, o trecho sobe/desce
    /// ou desvia para o lado (90° ou 45°), com as conexões refeitas. Útil para passar por viga, tubo ou duto.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class InfraDetourCommand : IExternalCommand
    {
        private const string Title = "Desvio de infraestrutura";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            var form = new FormWindow(Title, "Depois de OK, clique no eletroduto/eletrocalha no início e no fim do trecho a desviar.");
            var vertical = form.Radio("dir", "Subir/descer (desvio vertical)", true);
            form.Radio("dir", "Para o lado (desvio horizontal)", false);
            var distance = form.Number("Distância do desvio (m) — negativo desce / vai para o outro lado:", 0.30, -5, 5);
            var at45 = form.Check("Curvas de 45° (senão, 90°)", false);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            double offM = FormWindow.NumberOf(distance, 0.3);
            if (Math.Abs(offM) < 0.02) return Result.Cancelled;

            Reference r1, r2;
            try
            {
                r1 = uidoc.Selection.PickObject(ObjectType.PointOnElement, new StraightRunFilter(), "Clique no INÍCIO do desvio, sobre o eletroduto/eletrocalha");
                r2 = uidoc.Selection.PickObject(ObjectType.PointOnElement, new StraightRunFilter(), "Clique no FIM do desvio, sobre o mesmo trecho");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (r1.ElementId != r2.ElementId)
            {
                TaskDialog.Show(Title, "Os dois pontos precisam estar no mesmo trecho reto.");
                return Result.Cancelled;
            }

            var run = (MEPCurve)doc.GetElement(r1.ElementId);
            var line = (Line)((LocationCurve)run.Location).Curve;
            XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1), dir = (b - a).Normalize();
            double length = line.Length;
            double t1 = (r1.GlobalPoint - a).DotProduct(dir), t2 = (r2.GlobalPoint - a).DotProduct(dir);
            if (t1 > t2) (t1, t2) = (t2, t1);
            double off = RevitHelpers.M(offM);
            double margin = RevitHelpers.M(0.10);
            double need = at45.IsChecked == true ? 2 * Math.Abs(off) + margin : margin;
            t1 = Math.Max(t1, margin);
            t2 = Math.Min(t2, length - margin);
            if (t2 - t1 < need)
            {
                TaskDialog.Show(Title, "Trecho curto demais para esse desvio (aumente a distância entre os cliques ou use 90°).");
                return Result.Cancelled;
            }

            XYZ offset;
            if (vertical.IsChecked == true)
            {
                if (Math.Abs(dir.Z) > 0.9) { TaskDialog.Show(Title, "Para trecho vertical use o desvio horizontal."); return Result.Cancelled; }
                offset = XYZ.BasisZ * off;
            }
            else
            {
                XYZ side = XYZ.BasisZ.CrossProduct(dir);
                if (side.GetLength() < 1e-6) side = XYZ.BasisX;
                offset = side.Normalize() * off;
            }

            XYZ q1 = a + dir * t1, q2 = a + dir * t2;
            double slant = at45.IsChecked == true ? Math.Abs(off) : 0;
            XYZ q1o = q1 + offset + dir * slant, q2o = q2 + offset - dir * slant;

            int fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);

                Connector partnerA = Partner(run, a), partnerB = Partner(run, b);
                ElementId typeId = run.GetTypeId(), levelId = run.ReferenceLevel?.Id ?? run.LevelId;
                var sizes = SizeValues(run);
                bool tray = run is CableTray;
                doc.Delete(run.Id);

                MEPCurve New(XYZ p, XYZ q)
                {
                    MEPCurve c = tray ? CableTray.Create(doc, typeId, p, q, levelId) : Conduit.Create(doc, typeId, p, q, levelId);
                    foreach (var kv in sizes)
                    {
                        Parameter prm = c.get_Parameter(kv.Key);
                        if (prm != null && !prm.IsReadOnly) try { prm.Set(kv.Value); } catch (Exception) { }
                    }
                    return c;
                }

                MEPCurve s1 = New(a, q1), s2 = New(q1, q1o), s3 = New(q1o, q2o), s4 = New(q2o, q2), s5 = New(q2, b);
                doc.Regenerate();

                if (!Reconnect(s1, a, partnerA)) fail += partnerA != null ? 1 : 0;
                if (!Reconnect(s5, b, partnerB)) fail += partnerB != null ? 1 : 0;
                fail += BuildInfrastructureCommand.Connect(doc, s1, s2, q1) ? 0 : 1;
                fail += BuildInfrastructureCommand.Connect(doc, s2, s3, q1o) ? 0 : 1;
                fail += BuildInfrastructureCommand.Connect(doc, s3, s4, q2o) ? 0 : 1;
                fail += BuildInfrastructureCommand.Connect(doc, s4, s5, q2) ? 0 : 1;
                t.Commit();
            }

            TaskDialog.Show(Title, fail == 0
                ? "Desvio criado."
                : $"Desvio criado, mas {fail} ligação(ões) não puderam ser feitas (confira as conexões do tipo em Preferências de roteamento).");
            return Result.Succeeded;
        }

        /// <summary>Conector de outro elemento ligado à ponta do trecho (ou null).</summary>
        internal static Connector Partner(MEPCurve curve, XYZ end)
        {
            Connector c = curve.ConnectorManager.Connectors.Cast<Connector>()
                .Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(end)).FirstOrDefault();
            if (c == null || !c.IsConnected) return null;
            return c.AllRefs.Cast<Connector>().FirstOrDefault(r => r.Owner != null && r.Owner.Id != curve.Id && r.ConnectorType != ConnectorType.Logical);
        }

        internal static bool Reconnect(MEPCurve curve, XYZ end, Connector partner)
        {
            if (partner == null) return false;
            try
            {
                Connector c = curve.ConnectorManager.Connectors.Cast<Connector>()
                    .Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(end)).First();
                if (c.Origin.DistanceTo(partner.Origin) > RevitHelpers.M(0.01)) return false;
                c.ConnectTo(partner);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Dictionary<BuiltInParameter, double> SizeValues(MEPCurve c)
        {
            var map = new Dictionary<BuiltInParameter, double>();
            foreach (BuiltInParameter bip in new[]
                     {
                         BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM,
                         BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM,
                         BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM,
                     })
            {
                Parameter p = c.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) map[bip] = p.AsDouble();
            }
            return map;
        }
    }

    /// <summary>
    /// Ajustar rota: desloca um trecho reto (perpendicularmente) até passar pelo ponto clicado. As conexões das pontas
    /// acompanham; se alguma se soltar, o comando avisa.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AdjustRouteCommand : IExternalCommand
    {
        private const string Title = "Ajustar rota";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.ActiveView is not ViewPlan plan)
            {
                TaskDialog.Show(Title, "Abra uma planta para usar este comando.");
                return Result.Cancelled;
            }

            MEPCurve run;
            XYZ target;
            try
            {
                Reference r = uidoc.Selection.PickObject(ObjectType.Element, new StraightRunFilter(), "Clique no trecho de eletroduto/eletrocalha a deslocar");
                run = (MEPCurve)doc.GetElement(r);
                if (plan.SketchPlane == null && plan.GenLevel != null)
                {
                    using var sp = new Transaction(doc, "Plano de trabalho");
                    sp.Start();
                    plan.SketchPlane = SketchPlane.Create(doc, plan.GenLevel.Id);
                    sp.Commit();
                }
                target = uidoc.Selection.PickPoint("Clique no ponto por onde o trecho deve passar");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            var line = (Line)((LocationCurve)run.Location).Curve;
            XYZ a = line.GetEndPoint(0), dir = line.Direction;
            XYZ flat = new XYZ(target.X - a.X, target.Y - a.Y, 0);
            XYZ along = new XYZ(dir.X, dir.Y, 0);
            if (along.GetLength() < 1e-6)
            {
                TaskDialog.Show(Title, "Para trechos verticais, mova pela planta com o comando Mover do Revit.");
                return Result.Cancelled;
            }
            along = along.Normalize();
            XYZ move = flat - along * flat.DotProduct(along);
            if (move.GetLength() < RevitHelpers.M(0.005)) return Result.Succeeded;

            List<Connector> ends = run.ConnectorManager.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End).ToList();
            int connectedBefore = ends.Count(c => c.IsConnected);

            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                ElementTransformUtils.MoveElement(doc, run.Id, move);
                t.Commit();
            }

            int connectedAfter = run.ConnectorManager.Connectors.Cast<Connector>().Count(c => c.ConnectorType == ConnectorType.End && c.IsConnected);
            TaskDialog.Show(Title, $"Trecho deslocado {RevitHelpers.ToM(move.GetLength()):0.00} m." +
                (connectedAfter < connectedBefore
                    ? $"\n{connectedBefore - connectedAfter} ligação(ões) se soltaram: refaça com Construir Infraestrutura ou ajuste as conexões."
                    : ""));
            return Result.Succeeded;
        }
    }
}
