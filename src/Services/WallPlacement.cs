using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using CircuitoLivre.Commands;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Insere uma família de ponto (tomada, interruptor...) junto a uma parede, que pode estar no modelo
    /// ou num vínculo: hospedada na parede, na face da parede (famílias baseadas em face) ou sem hospedeiro
    /// (girada para dentro do ambiente).
    /// </summary>
    public static class WallPlacement
    {
        /// <param name="wall">Parede no documento dela (modelo ou vínculo); pode ser null.</param>
        /// <param name="link">Vínculo da parede (null se a parede é do modelo).</param>
        /// <param name="t">Transformação do documento da parede para o modelo.</param>
        /// <param name="onWall">Ponto na face da parede, na altura final, nas coordenadas do documento da parede.</param>
        /// <param name="tangent">Direção da parede (documento da parede).</param>
        /// <param name="inward">Normal apontando para o ambiente (documento da parede).</param>
        public static FamilyInstance Place(Document doc, Wall wall, RevitLinkInstance link, Transform t,
            XYZ onWall, XYZ tangent, XYZ inward, FamilySymbol symbol, Level level, double heightM, ICollection<string> notes)
        {
            XYZ pHost = t.OfPoint(onWall);
            pHost = new XYZ(pHost.X, pHost.Y, level.ProjectElevation + RevitHelpers.M(heightM));
            XYZ inwardHost = t.OfVector(inward);
            XYZ tangentHost = t.OfVector(tangent);

            FamilyPlacementType type = symbol.Family.FamilyPlacementType;
            try
            {
                if (type == FamilyPlacementType.OneLevelBasedHosted)
                {
                    if (wall == null || link != null)
                    {
                        notes.Add("Família hospedada em parede não pode ser lançada em parede de vínculo: use uma família baseada em face ou sem hospedeiro.");
                        return null;
                    }
                    FamilyInstance fi = doc.Create.NewFamilyInstance(pHost, symbol, wall, level, StructuralType.NonStructural);
                    LaunchPointsCommand.SetHeight(fi, heightM);
                    doc.Regenerate();
                    if (fi.CanFlipFacing && fi.FacingOrientation.DotProduct(inwardHost) < 0) fi.flipFacing();
                    return fi;
                }

                if (type == FamilyPlacementType.WorkPlaneBased && wall != null)
                {
                    Reference face = SideFace(wall, onWall, out XYZ onFace);
                    if (face != null)
                    {
                        if (link != null) face = face.CreateLinkReference(link);
                        XYZ location = t.OfPoint(onFace);
                        location = new XYZ(location.X, location.Y, pHost.Z);
                        return doc.Create.NewFamilyInstance(face, location, tangentHost, symbol);
                    }
                }

                FamilyInstance free = doc.Create.NewFamilyInstance(pHost, symbol, level, StructuralType.NonStructural);
                LaunchPointsCommand.SetHeight(free, heightM);
                doc.Regenerate();
                double angle = free.FacingOrientation.AngleOnPlaneTo(inwardHost, XYZ.BasisZ);
                if (Math.Abs(angle) > 1e-3)
                    ElementTransformUtils.RotateElement(doc, free.Id, Line.CreateBound(pHost, pHost + XYZ.BasisZ), angle);
                return free;
            }
            catch (Exception ex)
            {
                notes.Add("Falha ao inserir um ponto: " + ex.Message);
                return null;
            }
        }

        /// <summary>Face lateral da parede mais próxima do ponto (no documento da parede) e o ponto projetado nela.</summary>
        public static Reference SideFace(Wall wall, XYZ p, out XYZ onFace)
        {
            onFace = null;
            Reference best = null;
            double bestDistance = double.MaxValue;
            foreach (ShellLayerType side in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(wall, side); } catch (Exception) { continue; }
                foreach (Reference r in refs)
                {
                    if (wall.GetGeometryObjectFromReference(r) is not Face f) continue;
                    IntersectionResult ir = f.Project(p);
                    if (ir == null || ir.Distance >= bestDistance) continue;
                    bestDistance = ir.Distance;
                    best = r;
                    onFace = ir.XYZPoint;
                }
            }
            return best;
        }

        /// <summary>Largura da porta (instância ou tipo); 0,90 m se não encontrar.</summary>
        public static double DoorWidth(FamilyInstance door)
        {
            foreach (Element e in new Element[] { door, door.Symbol })
            {
                foreach (BuiltInParameter bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
                {
                    Parameter p = e?.get_Parameter(bip);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
                }
            }
            return RevitHelpers.M(0.90);
        }
    }
}
