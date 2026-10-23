using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CircuitoLivre.Services
{
    /// <summary>
    /// Geometria do trecho horizontal do lançamento de eletroduto entre dois pontos na mesma cota,
    /// conforme a trava de ângulo (livre/90°/45°) e a orientação do "L". Lógica pura (sem API do Revit no núcleo).
    /// </summary>
    public static class ConduitRouting
    {
        /// <summary>Vértices (x,y) do trecho de A a B, incluindo A e B, sem pontos repetidos consecutivos.</summary>
        public static List<(double X, double Y)> PlanWaypoints(double ax, double ay, double bx, double by,
            ConduitAngleLock angle, ConduitRunOrientation orientation)
        {
            var result = new List<(double X, double Y)>();
            void Add(double x, double y)
            {
                if (result.Count == 0 || Math.Abs(result[^1].X - x) > 1e-9 || Math.Abs(result[^1].Y - y) > 1e-9)
                    result.Add((x, y));
            }
            Add(ax, ay);

            if (angle == ConduitAngleLock.Free)
            {
                // trecho direto (diagonal livre)
            }
            else if (angle == ConduitAngleLock.Deg90)
            {
                bool xFirst = orientation != ConduitRunOrientation.Vertical; // Vertical = anda em Y primeiro
                if (xFirst) Add(bx, ay);
                else Add(ax, by);
            }
            else // 45°: diagonal de 45° no menor eixo e reta ortogonal no restante
            {
                double diag = Math.Min(Math.Abs(bx - ax), Math.Abs(by - ay));
                Add(ax + Math.Sign(bx - ax) * diag, ay + Math.Sign(by - ay) * diag);
            }

            Add(bx, by);
            return result;
        }

        /// <summary>Mesma rota, já na cota de A, como pontos do Revit.</summary>
        public static List<XYZ> HorizontalWaypoints(XYZ a, XYZ b, ConduitAngleLock angle, ConduitRunOrientation orientation)
        {
            var points = new List<XYZ>();
            foreach ((double X, double Y) in PlanWaypoints(a.X, a.Y, b.X, b.Y, angle, orientation))
                points.Add(new XYZ(X, Y, a.Z));
            return points;
        }

        /// <summary>
        /// Caminho 3D completo entre dois dispositivos (de A a B, incluindo as pontas).
        /// Pelo teto: sobe à cota h, corre no teto (ângulo em planta) e desce; com ângulo de parede livre,
        /// a subida/descida vira diagonal direto aos vértices do teto. Pela mesma parede (preferCeiling=false):
        /// trecho direto (livre = diagonal; 90° = em planta na cota de A e depois vertical).
        /// </summary>
        public static List<(double X, double Y, double Z)> LegWaypoints(
            double ax, double ay, double az, double bx, double by, double bz, double h,
            ConduitAngleLock planAngle, ConduitRunOrientation orientation, ConduitAngleLock wallAngle, bool preferCeiling)
        {
            var result = new List<(double X, double Y, double Z)>();
            void Add(double x, double y, double z)
            {
                if (result.Count == 0 || Math.Abs(result[^1].X - x) > 1e-9 || Math.Abs(result[^1].Y - y) > 1e-9 || Math.Abs(result[^1].Z - z) > 1e-9)
                    result.Add((x, y, z));
            }

            if (!preferCeiling)
            {
                Add(ax, ay, az);
                if (wallAngle != ConduitAngleLock.Free) Add(bx, by, az); // 90°: anda em planta na cota de A e depois sobe/desce
                Add(bx, by, bz);
                return result;
            }

            var plan = PlanWaypoints(ax, ay, bx, by, planAngle, orientation); // vértices em planta (inclui A e B)
            Add(ax, ay, az);
            if (wallAngle == ConduitAngleLock.Free)
            {
                // diagonal direto aos vértices internos do teto (sem subida/descida vertical)
                for (int k = 1; k < plan.Count - 1; k++) Add(plan[k].X, plan[k].Y, h);
            }
            else
            {
                Add(ax, ay, h);                                       // sobe reto até o teto
                for (int k = 1; k < plan.Count; k++) Add(plan[k].X, plan[k].Y, h); // corre no teto (inclui ponto acima de B)
            }
            Add(bx, by, bz);                                          // desce até o dispositivo
            return result;
        }

        /// <summary>Verdadeiro quando o ponto está na cota do teto (trecho horizontal = tipo de teto).</summary>
        public static bool AtCeiling(double z, double h) => Math.Abs(z - h) < 1e-6;
    }
}
