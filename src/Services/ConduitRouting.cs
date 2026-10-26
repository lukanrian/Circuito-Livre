using System;
using System.Collections.Generic;
using System.Linq;
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
                if (orientation == ConduitRunOrientation.Vertical)
                    Add(bx - Math.Sign(bx - ax) * diag, by - Math.Sign(by - ay) * diag);
                else Add(ax + Math.Sign(bx - ax) * diag, ay + Math.Sign(by - ay) * diag);
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
        /// as pontas reconhecidas em parede sobem/descem no próprio eixo e as demais usam os vértices do teto.
        /// Pela mesma parede (preferCeiling=false):
        /// trecho direto (livre = diagonal; 90° = em planta na cota de A e depois vertical).
        /// </summary>
        public static List<(double X, double Y, double Z)> LegWaypoints(
            double ax, double ay, double az, double bx, double by, double bz, double h,
            ConduitAngleLock planAngle, ConduitRunOrientation orientation, ConduitAngleLock wallAngle, bool preferCeiling, bool startOnWall = false, bool endOnWall = false, double minimumTransition = 0)
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
                if (wallAngle != ConduitAngleLock.Free)
                    foreach (var p in PlanWaypoints(ax, ay, bx, by, planAngle, orientation)) Add(p.X, p.Y, az);
                Add(bx, by, bz);
                return result;
            }

            var plan = PlanWaypoints(ax, ay, bx, by, planAngle, orientation); // vértices em planta (inclui A e B)
            Add(ax, ay, az);
            if (wallAngle == ConduitAngleLock.Free)
            {
                // A transição fica acima da saída de parede, sem projetar nem acrescentar desvios no meio da rota.
                if (startOnWall) Add(ax, ay, h);
                if (plan.Count < 3)
                {
                    if (!startOnWall && !endOnWall) Add((ax + bx) / 2, (ay + by) / 2, h);
                }
                else for (int k = 1; k < plan.Count - 1; k++) Add(plan[k].X, plan[k].Y, h);
                if (endOnWall) Add(bx, by, h);
            }
            else
            {
                // Uma caixa próxima ao teto não comporta duas curvas num desnível menor que a saída.
                if (startOnWall || Math.Abs(az - h) >= minimumTransition) Add(ax, ay, h);
                for (int k = 1; k < plan.Count; k++)
                    if (k < plan.Count - 1 || endOnWall || Math.Abs(bz - h) >= minimumTransition)
                        Add(plan[k].X, plan[k].Y, h);
            }
            Add(bx, by, bz);                                          // desce até o dispositivo
            return result;
        }

        /// <summary>Verdadeiro quando o ponto está na cota do teto (trecho horizontal = tipo de teto).</summary>
        public static bool AtCeiling(double z, double h) => Math.Abs(z - h) < 1e-6;

        /// <summary>
        /// Preserva a direção 3D dos conectores nas extremidades e aplica as opções de percurso entre elas.
        /// Junta trechos colineares sem inverter o sentido das saídas.
        /// </summary>
        public static List<(double X, double Y, double Z)> ConnectorRoute(
            double ax, double ay, double az, double dax, double day, double daz,
            double bx, double by, double bz, double dbx, double dby, double dbz, ConduitAngleLock angle, double stub,
            double h, ConduitRunOrientation orientation, ConduitAngleLock wallAngle, bool preferCeiling, bool startOnWall = false, bool endOnWall = false)
        {
            var r = new List<(double X, double Y, double Z)>();
            void Add(double x, double y, double z)
            {
                if (r.Count > 0 && Math.Abs(r[^1].X - x) < 1e-9 && Math.Abs(r[^1].Y - y) < 1e-9 && Math.Abs(r[^1].Z - z) < 1e-9) return;
                if (r.Count >= 2)
                {
                    var p0 = r[^2]; var p1 = r[^1];
                    double v1x = p1.X - p0.X, v1y = p1.Y - p0.Y, v1z = p1.Z - p0.Z;
                    double v2x = x - p1.X, v2y = y - p1.Y, v2z = z - p1.Z;
                    double cx = v1y * v2z - v1z * v2y, cy = v1z * v2x - v1x * v2z, cz = v1x * v2y - v1y * v2x;
                    double dot = v1x * v2x + v1y * v2y + v1z * v2z;
                    if (cx * cx + cy * cy + cz * cz < 1e-12 && dot > 0) { r[^1] = (x, y, z); return; } // colinear: estende
                }
                r.Add((x, y, z));
            }

            (double X, double Y, double Z) Stub(double x, double y, double z, double dx, double dy, double dz)
            {
                double m = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return m > 1e-9 ? (x + dx / m * stub, y + dy / m * stub, z + dz / m * stub) : (x, y, z);
            }
            var a = Stub(ax, ay, az, dax, day, daz);
            var b = Stub(bx, by, bz, dbx, dby, dbz);
            Add(ax, ay, az);
            // Uma saída para cima precisa de afastamento lateral antes de descer ao piso.
            (double X, double Y, double Z) FloorTurn((double X, double Y, double Z) p, double z,
                double dx, double dy, double dz, double otherX, double otherY)
            {
                if (!preferCeiling || h >= z || dz <= 1e-9) return p;
                double x = dy, y = -dx;
                if (Math.Abs(x) + Math.Abs(y) < 1e-9) { x = otherX - p.X; y = otherY - p.Y; }
                double length = Math.Sqrt(x * x + y * y);
                if (length < 1e-9) { x = 1; y = 0; length = 1; }
                return (p.X + x / length * stub, p.Y + y / length * stub, p.Z);
            }
            var turnA = FloorTurn(a, az, dax, day, daz, b.X, b.Y);
            var turnB = FloorTurn(b, bz, dbx, dby, dbz, a.X, a.Y);
            if (turnA != a) Add(a.X, a.Y, a.Z);
            foreach (var p in LegWaypoints(turnA.X, turnA.Y, turnA.Z, turnB.X, turnB.Y, turnB.Z, h,
                angle, orientation, wallAngle, preferCeiling, startOnWall, endOnWall, stub)) Add(p.X, p.Y, p.Z);
            if (turnB != b) Add(b.X, b.Y, b.Z);
            Add(bx, by, bz);
            return r;
        }
        /// <summary>Tenta afastamentos crescentes; só alterna o L quando a orientação está em Padrão.</summary>
        public static IEnumerable<List<(double X, double Y, double Z)>> CandidateRoutes(
            (double X, double Y, double Z) a, (double X, double Y, double Z) da,
            (double X, double Y, double Z) b, (double X, double Y, double Z) db,
            ConduitAngleLock angle, double h, ConduitRunOrientation orientation, ConduitAngleLock wallAngle,
            bool preferCeiling, double initialStub, double minimumLength, bool startOnWall = false, bool endOnWall = false)
        {
            if (!Finite(a) || !Finite(b) || !Finite(da) || !Finite(db) ||
                !double.IsFinite(initialStub) || initialStub <= 0 || !double.IsFinite(h) ||
                !double.IsFinite(minimumLength) || minimumLength <= 0) yield break;
            var seen = new List<List<(double X, double Y, double Z)>>();
            double start = Math.Max(initialStub, minimumLength);
            int variations = orientation == ConduitRunOrientation.Default && angle != ConduitAngleLock.Free ? 2 : 1;
            int wallVariations = wallAngle == ConduitAngleLock.Free && (preferCeiling || Math.Abs(a.Z - b.Z) > minimumLength) ? 2 : 1;
            for (int attempt = 0; attempt < 6; attempt++)
                for (int variant = 0; variant < variations; variant++)
                for (int wallVariant = 0; wallVariant < wallVariations; wallVariant++)
                {
                    var way = ConnectorRoute(a.X, a.Y, a.Z, da.X, da.Y, da.Z, b.X, b.Y, b.Z, db.X, db.Y, db.Z,
                        angle, start * (1 + attempt * .5), h,
                        variant == 0 ? orientation : ConduitRunOrientation.Vertical,
                        wallVariant == 0 ? wallAngle : ConduitAngleLock.Deg90, preferCeiling, startOnWall, endOnWall);
                    if (IsValidRoute(way, da, db, minimumLength) && !seen.Any(previous => previous.SequenceEqual(way)))
                    {
                        seen.Add(way);
                        yield return way;
                    }
                }
        }

        /// <summary>Recusa trechos curtos, retornos, cruzamentos e pontas fora do sentido dos conectores.</summary>
        public static bool IsValidRoute(IReadOnlyList<(double X, double Y, double Z)> points,
            (double X, double Y, double Z) startDirection, (double X, double Y, double Z) endDirection, double minimumLength)
        {
            if (points == null || points.Count < 2 || !double.IsFinite(minimumLength) || minimumLength <= 0 ||
                !Finite(startDirection) || !Finite(endDirection)) return false;
            for (int i = 0; i < points.Count; i++) if (!Finite(points[i])) return false;
            for (int i = 1; i < points.Count; i++)
            {
                var delta = Subtract(points[i], points[i - 1]);
                double length = Length(delta);
                if (!double.IsFinite(length) || length < minimumLength) return false;
                if (i > 1)
                {
                    var previous = Subtract(points[i - 1], points[i - 2]);
                    if (Dot(previous, delta) / Length(previous) / length < -1e-6) return false;
                }
                for (int j = 1; j < i - 1; j++)
                    if (SegmentsMeet(points[j - 1], points[j], points[i - 1], points[i], 1e-6)) return false;
            }
            return (Length(startDirection) < 1e-9 || SameDirection(Subtract(points[1], points[0]), startDirection)) &&
                (Length(endDirection) < 1e-9 || SameDirection(Subtract(points[^2], points[^1]), endDirection));
        }

        public static int FloorOutletPriority((double X, double Y, double Z) direction) =>
            SameDirection(direction, (0, 0, -1)) ? 0 : 1;

        public static bool SameDirection((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        {
            double lengths = Length(a) * Length(b);
            return double.IsFinite(lengths) && lengths > 1e-12 && Dot(a, b) / lengths >= 1 - 1e-6;
        }

        private static bool SegmentsMeet((double X, double Y, double Z) a, (double X, double Y, double Z) b,
            (double X, double Y, double Z) c, (double X, double Y, double Z) d, double tolerance)
        {
            var u = Subtract(b, a); var v = Subtract(d, c); var w = Subtract(c, a);
            var normal = Cross(u, v);
            double squared = Dot(normal, normal);
            if (squared <= 1e-12 * Dot(u, u) * Dot(v, v))
            {
                if (Length(Cross(w, u)) / Length(u) > tolerance) return false;
                double t0 = Dot(w, u) / Dot(u, u), t1 = Dot(Subtract(d, a), u) / Dot(u, u);
                return Math.Max(0, Math.Min(t0, t1)) <= Math.Min(1, Math.Max(t0, t1));
            }
            double t = Dot(Cross(w, v), normal) / squared;
            double s = Dot(Cross(w, u), normal) / squared;
            if (t < 0 || t > 1 || s < 0 || s > 1) return false;
            return Length((u.X * t - v.X * s - w.X, u.Y * t - v.Y * s - w.Y, u.Z * t - v.Z * s - w.Z)) <= tolerance;
        }

        private static bool Finite((double X, double Y, double Z) p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
        private static (double X, double Y, double Z) Subtract((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static double Length((double X, double Y, double Z) v) => Math.Sqrt(Dot(v, v));
        private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
