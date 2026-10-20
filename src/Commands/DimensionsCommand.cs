using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Adicionar cotas: cota automaticamente os dispositivos elétricos na planta ativa.
    /// Por seleção: uma cota linear única pelos dispositivos alinhados. Por ambiente: cada dispositivo de parede
    /// recebe uma cota até a parede perpendicular mais próxima.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AddDimensionsCommand : IExternalCommand
    {
        private const string Title = "Adicionar cotas";

        private static readonly BuiltInCategory[] Devices =
        {
            BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_ElectricalEquipment,
        };

        private class DeviceFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is FamilyInstance && Devices.Contains(e.Category?.BuiltInCategory ?? BuiltInCategory.INVALID);
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc?.ActiveView is not ViewPlan view || view.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra a planta do pavimento.");
                return Result.Cancelled;
            }

            var form = new FormWindow(Title, "Escolha o modo. As cotas usam o tipo de cota linear padrão do projeto.");
            var bySelection = form.Radio("mode", "Por dispositivos: selecione dispositivos alinhados → uma cota linear única", true);
            form.Radio("mode", "Por ambiente: cada dispositivo de parede cotado até a parede perpendicular mais próxima", false);
            var offset = form.Number("Deslocamento da linha de cota (m):", 0.40, 0, 5);
            RevitHelpers.ShowOwned(form, data);
            if (form.ShowDialog() != true) return Result.Cancelled;
            double off = RevitHelpers.M(FormWindow.NumberOf(offset, 0.4));

            List<FamilyInstance> picked;
            try
            {
                picked = uidoc.Selection.PickObjects(ObjectType.Element, new DeviceFilter(), "Selecione os dispositivos e clique em Concluir")
                    .Select(r => doc.GetElement(r)).OfType<FamilyInstance>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (picked.Count == 0) return Result.Cancelled;

            int created = 0, fail = 0;
            using (var t = new Transaction(doc, Title))
            {
                t.Start();
                if (bySelection.IsChecked == true)
                {
                    if (picked.Count < 2) { TaskDialog.Show(Title, "Selecione pelo menos dois dispositivos alinhados."); return Result.Cancelled; }
                    if (Aligned(doc, view, picked, off)) created++; else fail++;
                }
                else
                {
                    List<OutletsByRoomCommand.RoomItem> rooms = OutletsByRoomCommand.CollectRooms(doc)
                        .Where(r => Math.Abs(r.HostZ - view.GenLevel.ProjectElevation) <= RevitHelpers.M(0.5)).ToList();
                    foreach (FamilyInstance fi in picked)
                    {
                        if (ToWall(doc, view, fi, rooms, off)) created++; else fail++;
                    }
                }
                t.Commit();
            }

            TaskDialog.Show(Title, $"{created} cota(s) criada(s)." +
                (fail > 0 ? $"\n{fail} não puderam ser criadas (dispositivos não alinhados, fora de ambiente ou família sem planos de referência)." : ""));
            return Result.Succeeded;
        }

        /// <summary>Plano de referência do dispositivo perpendicular à direção da cota.</summary>
        private static Reference PlaneAcross(FamilyInstance fi, XYZ direction)
        {
            XYZ hand = fi.HandOrientation, facing = fi.FacingOrientation;
            FamilyInstanceReferenceType type = Math.Abs(hand.DotProduct(direction)) >= Math.Abs(facing.DotProduct(direction))
                ? FamilyInstanceReferenceType.CenterLeftRight
                : FamilyInstanceReferenceType.CenterFrontBack;
            return fi.GetReferences(type).FirstOrDefault();
        }

        private static bool Aligned(Document doc, View view, List<FamilyInstance> devices, double off)
        {
            List<(FamilyInstance F, XYZ P)> pts = devices.Select(f => (f, RevitHelpers.PointOf(f))).Where(x => x.Item2 != null).ToList();
            XYZ a = pts.First().P, b = pts.OrderByDescending(x => x.P.DistanceTo(a)).First().P;
            XYZ dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (dir.GetLength() < RevitHelpers.M(0.05)) return false;
            dir = dir.Normalize();
            XYZ normal = XYZ.BasisZ.CrossProduct(dir);
            var refs = new ReferenceArray();
            foreach (var (f, p) in pts.OrderBy(x => (x.P - a).DotProduct(dir)))
            {
                Reference r = PlaneAcross(f, dir);
                if (r != null) refs.Append(r);
            }
            if (refs.Size < 2) return false;
            XYZ z0 = new XYZ(a.X, a.Y, 0) + normal * off;
            try
            {
                doc.Create.NewDimension(view, Line.CreateBound(z0, z0 + dir), refs);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool ToWall(Document doc, View view, FamilyInstance fi, List<OutletsByRoomCommand.RoomItem> rooms, double off)
        {
            XYZ p = RevitHelpers.PointOf(fi);
            if (p == null) return false;
            foreach (OutletsByRoomCommand.RoomItem room in rooms)
            {
                XYZ local = room.T.Inverse.OfPoint(new XYZ(p.X, p.Y, room.Room.Level.ProjectElevation + RevitHelpers.M(1.0)));
                if (!OutletsByRoomCommand.Inside(room, local)) continue;

                IList<IList<BoundarySegment>> loops = room.Room.GetBoundarySegments(new SpatialElementBoundaryOptions
                    { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
                if (loops == null) return false;
                List<(Curve C, Wall W)> segs = loops.SelectMany(l => l)
                    .Select(s => (s.GetCurve(), room.Doc.GetElement(s.ElementId) as Wall)).Where(x => x.Item2 != null && x.Item1 is Line).ToList();
                if (segs.Count == 0) return false;

                // parede onde está o dispositivo e a direção ao longo dela
                var host = segs.OrderBy(x => x.C.Distance(new XYZ(local.X, local.Y, x.C.GetEndPoint(0).Z))).First();
                XYZ along = ((Line)host.C).Direction;
                along = new XYZ(along.X, along.Y, 0).Normalize();

                // parede perpendicular mais próxima ao longo dessa direção
                (Curve C, Wall W) best = default;
                double bestDist = double.MaxValue;
                foreach (var s in segs)
                {
                    XYZ d = ((Line)s.C).Direction;
                    if (Math.Abs(new XYZ(d.X, d.Y, 0).Normalize().DotProduct(along)) > 0.2) continue;
                    XYZ q = s.C.Evaluate(0.5, true);
                    double dist = Math.Abs((new XYZ(q.X, q.Y, 0) - new XYZ(local.X, local.Y, 0)).DotProduct(along));
                    if (dist < bestDist) { bestDist = dist; best = s; }
                }
                if (best.W == null) return false;

                Reference face = WallPlacement.SideFace(best.W, new XYZ(local.X, local.Y, local.Z), out _);
                if (face == null) return false;
                if (room.Link != null) face = face.CreateLinkReference(room.Link);
                Reference device = PlaneAcross(fi, room.T.OfVector(along));
                if (device == null) return false;

                var refs = new ReferenceArray();
                refs.Append(device);
                refs.Append(face);
                XYZ alongHost = room.T.OfVector(along);
                XYZ normal = XYZ.BasisZ.CrossProduct(alongHost);
                XYZ z0 = new XYZ(p.X, p.Y, 0) + normal * off;
                try
                {
                    doc.Create.NewDimension(view, Line.CreateBound(z0, z0 + alongHost), refs);
                    return true;
                }
                catch (Exception) { return false; }
            }
            return false;
        }
    }
}
