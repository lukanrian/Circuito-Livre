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
    /// <summary>
    /// Construir bandejas de cabos: selecione por retângulo os dispositivos com conectores de bandeja; os conectores
    /// alinhados e de frente um para o outro são ligados por trechos de eletrocalha do tipo escolhido.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class BuildTraysCommand : IExternalCommand
    {
        private const string Title = "Construir bandejas de cabos";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan plan || plan.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra uma planta.");
                return Result.Cancelled;
            }
            List<CableTrayType> types = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().OrderBy(t => t.Name).ToList();
            if (types.Count == 0) { TaskDialog.Show(Title, "Nenhum tipo de eletrocalha no projeto."); return Result.Cancelled; }

            var form = new FormWindow(Title, "Depois de OK, selecione por retângulo os dispositivos/equipamentos com conectores de bandeja.");
            var typeCombo = form.Combo("Tipo de eletrocalha:", types, t => t.Name);
            var useConnector = form.Check("Usar a largura e a altura do conector", true);
            DimensioningSettings cfg = DimensioningSettings.Load();
            var width = form.Number("Largura (mm), se não usar a do conector:", cfg.TrayDefaultWidthMm, 10, 2000);
            var height = form.Number("Altura (mm):", cfg.TrayDefaultHeightMm, 10, 1000);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;

            IList<Element> picked;
            try { picked = uidoc.Selection.PickElementsByRectangle("Selecione por retângulo os elementos com conector de bandeja"); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            List<Connector> connectors = picked.OfType<FamilyInstance>()
                .SelectMany(f => f.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>() ?? Enumerable.Empty<Connector>())
                .Where(c => c.Domain == Domain.DomainCableTrayConduit && !c.IsConnected && c.Shape == ConnectorProfileType.Rectangular)
                .ToList();
            if (connectors.Count < 2) { TaskDialog.Show(Title, "Foram encontrados menos de dois conectores de bandeja livres."); return Result.Cancelled; }

            // pares: conectores de frente um para o outro e alinhados
            var used = new HashSet<Connector>();
            var pairs = new List<(Connector A, Connector B)>();
            foreach (Connector a in connectors.OrderBy(c => c.Origin.X).ThenBy(c => c.Origin.Y))
            {
                if (used.Contains(a)) continue;
                XYZ da = a.CoordinateSystem.BasisZ;
                Connector best = null;
                double bestDist = double.MaxValue;
                foreach (Connector b in connectors)
                {
                    if (b == a || used.Contains(b) || b.Owner.Id == a.Owner.Id) continue;
                    XYZ v = b.Origin - a.Origin;
                    double dist = v.GetLength();
                    if (dist < RevitHelpers.M(0.05)) continue;
                    XYZ u = v.Normalize();
                    if (u.DotProduct(da) < 0.99 || u.DotProduct(b.CoordinateSystem.BasisZ) > -0.99) continue;
                    if (dist < bestDist) { bestDist = dist; best = b; }
                }
                if (best == null) continue;
                used.Add(a);
                used.Add(best);
                pairs.Add((a, best));
            }
            if (pairs.Count == 0) { TaskDialog.Show(Title, "Nenhum par de conectores alinhados e de frente um para o outro."); return Result.Cancelled; }

            var type = FormWindow.Selected<CableTrayType>(typeCombo);
            int created = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                foreach (var (a, b) in pairs)
                {
                    try
                    {
                        CableTray tray = CableTray.Create(doc, type.Id, a.Origin, b.Origin, plan.GenLevel.Id);
                        double w = useConnector.IsChecked == true && a.Width > 0 ? a.Width : RevitHelpers.M(FormWindow.NumberOf(width) / 1000);
                        double h = useConnector.IsChecked == true && a.Height > 0 ? a.Height : RevitHelpers.M(FormWindow.NumberOf(height) / 1000);
                        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.Set(w);
                        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.Set(h);
                        doc.Regenerate();
                        foreach (Connector end in tray.ConnectorManager.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End))
                        {
                            Connector target = end.Origin.DistanceTo(a.Origin) < end.Origin.DistanceTo(b.Origin) ? a : b;
                            try { end.ConnectTo(target); } catch (Exception) { }
                        }
                        created++;
                    }
                    catch (Exception) { fail++; }
                }
                t.Commit();
            }
            TaskDialog.Show(Title, $"{created} trecho(s) de eletrocalha criado(s)." + (fail > 0 ? $"\n{fail} falharam." : ""));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Uniões: divide os eletrodutos/eletrocalhas horizontais selecionados no espaçamento escolhido e insere a união
    /// definida nas Preferências de roteamento do tipo em cada divisão. Trechos verticais são pulados.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SplitWithUnionsCommand : IExternalCommand
    {
        private const string Title = "Adicionar uniões";

        private class RunFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => (e is Conduit || e is CableTray) && (e.Location as LocationCurve)?.Curve is Line;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            var form = new FormWindow(Title, "Barras comerciais: eletroduto rígido 3 m, eletrocalha 3 m. As uniões vêm das Preferências de roteamento do tipo.");
            var spacing = form.Number("Comprimento de cada barra (m):", 3.0, 0.5, 12);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            double step = RevitHelpers.M(FormWindow.NumberOf(spacing, 3));

            List<MEPCurve> runs;
            try
            {
                runs = uidoc.Selection.PickObjects(ObjectType.Element, new RunFilter(), "Selecione os trechos e clique em Concluir")
                    .Select(r => doc.GetElement(r)).OfType<MEPCurve>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            int unions = 0, fail = 0, skipped = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                WarningSwallower.Attach(t);
                foreach (MEPCurve run in runs)
                {
                    var line = (Line)((LocationCurve)run.Location).Curve;
                    if (Math.Abs(line.Direction.Z) > 0.1 || line.Length <= step * 1.05) { skipped++; continue; }

                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1), dir = line.Direction;
                    Connector partnerA = InfraDetourCommand.Partner(run, a), partnerB = InfraDetourCommand.Partner(run, b);
                    ElementId typeId = run.GetTypeId(), levelId = run.ReferenceLevel?.Id ?? run.LevelId;
                    bool tray = run is CableTray;
                    var sizes = new Dictionary<BuiltInParameter, double>();
                    foreach (BuiltInParameter bip in new[] { BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM })
                    {
                        Parameter p = run.get_Parameter(bip);
                        if (p != null && p.HasValue && p.StorageType == StorageType.Double) sizes[bip] = p.AsDouble();
                    }
                    doc.Delete(run.Id);

                    var points = new List<XYZ> { a };
                    for (double d = step; d < line.Length - RevitHelpers.M(0.05); d += step) points.Add(a + dir * d);
                    points.Add(b);

                    var pieces = new List<MEPCurve>();
                    for (int i = 0; i + 1 < points.Count; i++)
                    {
                        MEPCurve c = tray ? CableTray.Create(doc, typeId, points[i], points[i + 1], levelId) : Conduit.Create(doc, typeId, points[i], points[i + 1], levelId);
                        foreach (var kv in sizes)
                        {
                            Parameter p = c.get_Parameter(kv.Key);
                            if (p != null && !p.IsReadOnly) try { p.Set(kv.Value); } catch (Exception) { }
                        }
                        pieces.Add(c);
                    }
                    doc.Regenerate();
                    InfraDetourCommand.Reconnect(pieces.First(), a, partnerA);
                    InfraDetourCommand.Reconnect(pieces.Last(), b, partnerB);
                    for (int i = 0; i + 1 < pieces.Count; i++)
                    {
                        try
                        {
                            Connector c1 = End(pieces[i], points[i + 1]), c2 = End(pieces[i + 1], points[i + 1]);
                            doc.Create.NewUnionFitting(c1, c2);
                            unions++;
                        }
                        catch (Exception) { fail++; }
                    }
                }
                t.Commit();
            }
            TaskDialog.Show(Title, $"{unions} união(ões) inserida(s)." +
                (skipped > 0 ? $"\n{skipped} trecho(s) pulados (verticais ou menores que uma barra)." : "") +
                (fail > 0 ? $"\n{fail} união(ões) não puderam ser inseridas (defina a união nas Preferências de roteamento do tipo)." : ""));
            return Result.Succeeded;
        }

        private static Connector End(MEPCurve c, XYZ p) =>
            c.ConnectorManager.Connectors.Cast<Connector>().Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(p)).First();
    }
}
