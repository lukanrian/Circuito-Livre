using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Distribui tomadas de uso geral (TUG) no perímetro dos ambientes escolhidos, com a quantidade mínima e as
    /// potências da NBR 5410 (9.5.2.2), evitando portas e cantos. Os ambientes podem estar no próprio modelo
    /// (ambientes ou espaços) ou num vínculo da arquitetura.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class OutletsByRoomCommand : IExternalCommand
    {
        private const string Title = "Tomadas por ambiente";

        private static int _mode;       // 0 = NBR, 1 = espaçamento, 2 = quantidade
        private static double _value;

        private enum RoomKind { Dry, Wet, Bath, Balcony, Other }

        internal class RoomItem
        {
            public SpatialElement Room;
            public Document Doc;
            public Transform T = Transform.Identity;
            public RevitLinkInstance Link;
            public string Name;
            public double HostZ;
        }

        private class Seg
        {
            public Curve Curve;
            public Wall Wall;
            public double Start;   // posição no perímetro (pés)
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            if (doc.ActiveView is not ViewPlan plan || plan.GenLevel == null)
            {
                TaskDialog.Show(Title, "Abra a planta do pavimento onde as tomadas serão lançadas.");
                return Result.Cancelled;
            }
            Level level = plan.GenLevel;

            List<RoomItem> rooms = CollectRooms(doc);
            if (rooms.Count == 0)
            {
                TaskDialog.Show(Title, "Nenhum ambiente encontrado no modelo nem nos vínculos carregados.");
                return Result.Cancelled;
            }

            List<FamilySymbol> symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalFixtures)
                .OrderBy(s => s.FamilyName + " " + s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (symbols.Count == 0)
            {
                TaskDialog.Show(Title, "Carregue no projeto uma família de tomada (categoria Dispositivos elétricos).");
                return Result.Cancelled;
            }

            var items = rooms
                .OrderBy(r => Math.Abs(r.HostZ - level.ProjectElevation) > RevitHelpers.M(0.5))
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new CheckItem(
                    $"{r.Name}  —  {KindLabel(Classify(r.Name))}{(r.Link != null ? "  (vínculo: " + r.Link.Name.Split(':')[0].Trim() + ")" : "")}",
                    r, Math.Abs(r.HostZ - level.ProjectElevation) <= RevitHelpers.M(0.5)))
                .ToList();

            var window = new ChecklistWindow(Title,
                    "Marque os ambientes. A quantidade segue a NBR 5410: sala/dormitório 1 a cada 5 m de perímetro; cozinha, copa, " +
                    "área de serviço e lavanderia 1 a cada 3,5 m; banheiro e varanda 1; demais 1 até 6 m² e 1 a cada 5 m acima. " +
                    "Os ambientes do nível desta planta já vêm marcados.", items, "Lançar")
                .WithCombo("Tomada:", symbols.Select(s => new CheckItem($"{s.FamilyName} : {s.Name}", s)).ToList())
                .WithText("Altura da tomada (m):", "0,30")
                .WithOption1("Potências da NBR 5410 (600 VA nas 3 primeiras de cozinha/serviço/banheiro; 100 VA nas demais)", true)
                .WithOption2("Evitar portas e cantos", true);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            double? heightM = FormWindow.ParseNumber(window.Text);
            if (heightM == null || heightM < 0 || heightM > 5)
            {
                TaskDialog.Show(Title, "Altura inválida.");
                return Result.Cancelled;
            }
            var symbol = (FamilySymbol)window.ComboSelection.Tag;
            bool nbrPower = window.Option1Checked, avoid = window.Option2Checked;

            var qty = new FormWindow(Title, "Como definir a quantidade de tomadas em cada ambiente?");
            var byNbr = qty.Radio("qty", "Pela NBR 5410 (perímetro e tipo do ambiente)", true);
            var bySpacing = qty.Radio("qty", "Espaçamento fixo ao longo do perímetro", false);
            var spacing = qty.Number("Espaçamento (m):", 3.0, 0.3, 50);
            qty.Radio("qty", "Quantidade fixa por ambiente", false);
            var fixedCount = qty.Number("Quantidade:", 2, 1, 100);
            RevitHelpers.ShowOwned(qty, data);
            if (qty.ShowDialog() != true) return Result.Cancelled;
            _mode = byNbr.IsChecked == true ? 0 : bySpacing.IsChecked == true ? 1 : 2;
            _value = _mode == 1 ? FormWindow.NumberOf(spacing, 3) : FormWindow.NumberOf(fixedCount, 2);

            var lines = new List<string>();
            var notes = new HashSet<string>();
            var placed = new List<ElementId>();
            WarningSwallower warnings;

            using (var t = new Transaction(doc, "Tomadas por ambiente"))
            {
                t.Start();
                warnings = WarningSwallower.Attach(t);
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                foreach (RoomItem room in window.CheckedItems.Select(i => (RoomItem)i.Tag))
                {
                    try
                    {
                        int before = placed.Count;
                        string summary = PlaceInRoom(doc, room, symbol, level, heightM.Value, nbrPower, avoid, placed, notes);
                        lines.Add($"{room.Name}: {summary}" + (placed.Count == before ? " — nenhuma lançada" : ""));
                    }
                    catch (Exception ex) { lines.Add($"{room.Name}: erro — {ex.Message}"); }
                }
                t.Commit();
            }

            var report = new TaskDialog(Title)
            {
                MainInstruction = $"{placed.Count} tomada(s) lançada(s) em {lines.Count} ambiente(s).",
                MainContent = RevitHelpers.Join(lines, 25) +
                              (warnings.Discarded > 0 ? $"\n\n{warnings.Discarded} aviso(s) do Revit descartado(s)." : ""),
            };
            if (notes.Count > 0) report.ExpandedContent = string.Join("\n", notes);
            report.Show();
            if (placed.Count > 0) uidoc.Selection.SetElementIds(placed);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ ambientes

        internal static List<RoomItem> CollectRooms(Document doc)
        {
            var list = new List<RoomItem>();
            void Add(Document d, RevitLinkInstance link)
            {
                Transform t = link?.GetTotalTransform() ?? Transform.Identity;
                IEnumerable<SpatialElement> spatial = new FilteredElementCollector(d)
                    .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces }))
                    .WhereElementIsNotElementType()
                    .OfType<SpatialElement>()
                    .Where(r => r.Area > 0 && r.Level != null);
                foreach (SpatialElement r in spatial)
                {
                    list.Add(new RoomItem
                    {
                        Room = r,
                        Doc = d,
                        T = t,
                        Link = link,
                        Name = NameOf(r),
                        HostZ = t.OfPoint(new XYZ(0, 0, r.Level.ProjectElevation)).Z,
                    });
                }
            }

            Add(doc, null);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ld = link.GetLinkDocument();
                if (ld != null) Add(ld, link);
            }
            return list;
        }

        private static string NameOf(SpatialElement r)
        {
            string name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            string number = r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
            if (string.IsNullOrWhiteSpace(name)) name = r.Name;
            return string.IsNullOrWhiteSpace(number) ? name : $"{number} {name}";
        }

        private static string Plain(string s)
        {
            string d = (s ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
            return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        }

        private static RoomKind Classify(string name)
        {
            string n = Plain(name);
            bool Has(params string[] keys) => keys.Any(k => n.Contains(k));
            if (Has("banh", "wc", "lavabo", "sanit")) return RoomKind.Bath;
            if (Has("cozinha", "copa", "servico", "lavanderia", "gourmet", "a.s.")) return RoomKind.Wet;
            if (Has("varanda", "sacada", "terraco", "balcao")) return RoomKind.Balcony;
            if (Has("sala", "quarto", "dormit", "suite", "estar", "jantar", "escritorio", "home", "estudo")) return RoomKind.Dry;
            return RoomKind.Other;
        }

        private static string KindLabel(RoomKind k) => k switch
        {
            RoomKind.Dry => "sala/dormitório (5 m)",
            RoomKind.Wet => "cozinha/serviço (3,5 m)",
            RoomKind.Bath => "banheiro (1)",
            RoomKind.Balcony => "varanda (1)",
            _ => "demais (pela área)",
        };

        // ------------------------------------------------------------------ distribuição

        private static string PlaceInRoom(Document doc, RoomItem room, FamilySymbol symbol, Level level, double heightM,
            bool nbrPower, bool avoid, List<ElementId> placed, HashSet<string> notes)
        {
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            IList<IList<BoundarySegment>> loops = room.Room.GetBoundarySegments(options);
            if (loops == null || loops.Count == 0) return "sem contorno (ambiente não fechado)";

            IList<BoundarySegment> loop = loops.OrderByDescending(l => l.Sum(s => s.GetCurve().Length)).First();
            var segs = new List<Seg>();
            double perimeter = 0;
            foreach (BoundarySegment b in loop)
            {
                Curve c = b.GetCurve();
                segs.Add(new Seg { Curve = c, Wall = room.Doc.GetElement(b.ElementId) as Wall, Start = perimeter });
                perimeter += c.Length;
            }

            double perimeterM = RevitHelpers.ToM(perimeter);
            double areaM2 = room.Room.Area * 0.09290304;
            RoomKind kind = Classify(room.Name);
            int count = kind switch
            {
                RoomKind.Bath or RoomKind.Balcony => 1,
                RoomKind.Wet => (int)Math.Ceiling(perimeterM / 3.5),
                RoomKind.Dry => (int)Math.Ceiling(perimeterM / 5.0),
                _ => areaM2 <= 6.0 ? 1 : (int)Math.Ceiling(perimeterM / 5.0),
            };
            if (_mode == 1) count = (int)Math.Ceiling(perimeterM / Math.Max(0.3, _value));
            else if (_mode == 2) count = (int)Math.Round(_value);
            count = Math.Max(1, count);

            List<(double From, double To)> blocked = avoid ? Blocked(room, segs, perimeter) : new List<(double, double)>();
            // trechos sem parede (linha de separação) nunca recebem tomada
            blocked.AddRange(segs.Where(s => s.Wall == null).Select(s => (s.Start, s.Start + s.Curve.Length)));

            double step = perimeter / count;
            double minGap = Math.Min(RevitHelpers.M(0.5), step / 2);
            var positions = new List<double>();
            for (int k = 0; k < count; k++)
            {
                double? pos = Free((k + 0.5) * step, perimeter, blocked, positions, minGap);
                if (pos.HasValue) positions.Add(pos.Value);
            }

            int ok = 0;
            double totalVa = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                double va = !nbrPower ? 100 : (kind == RoomKind.Wet || kind == RoomKind.Bath) && i < 3 ? 600 : 100;
                FamilyInstance fi = PlaceAt(doc, room, segs, positions[i], symbol, level, heightM, notes);
                if (fi == null) continue;
                if (!LaunchPointsCommand.SetPower(fi, va))
                    notes.Add("A família de tomada não aceitou a potência (parâmetro de tipo ou com outro nome).");
                placed.Add(fi.Id);
                ok++;
                totalVa += va;
            }

            return $"{perimeterM:0.0} m, {areaM2:0.0} m² → {ok}/{count} tomada(s), {totalVa:0} VA" +
                   (positions.Count < count ? " (sem espaço livre para todas)" : "");
        }

        /// <summary>Faixas do perímetro proibidas: portas (largura + 15 cm de cada lado) e cantos (30 cm).</summary>
        private static List<(double, double)> Blocked(RoomItem room, List<Seg> segs, double perimeter)
        {
            var list = new List<(double, double)>();
            double corner = RevitHelpers.M(0.30), doorGap = RevitHelpers.M(0.15);

            for (int i = 0; i < segs.Count; i++)
            {
                Seg prev = segs[(i - 1 + segs.Count) % segs.Count], cur = segs[i];
                XYZ a = Tangent(prev.Curve, 1), b = Tangent(cur.Curve, 0);
                if (a.DotProduct(b) < Math.Cos(Math.PI / 9)) // mudança de direção > 20°: canto
                    list.Add((cur.Start - corner, cur.Start + corner));
            }

            var wallSegs = segs.Where(s => s.Wall != null).ToList();
            var wallIds = new HashSet<ElementId>(wallSegs.Select(s => s.Wall.Id));
            IEnumerable<FamilyInstance> doors = new FilteredElementCollector(room.Doc).OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(d => d.Host != null && wallIds.Contains(d.Host.Id));
            foreach (FamilyInstance door in doors)
            {
                if (door.Location is not LocationPoint lp) continue;
                double half = WallPlacement.DoorWidth(door) / 2 + doorGap;
                foreach (Seg s in wallSegs.Where(s => s.Wall.Id == door.Host.Id))
                {
                    IntersectionResult r = s.Curve.Project(lp.Point);
                    if (r == null || r.Distance > RevitHelpers.M(1.0)) continue;
                    double along = s.Curve.ComputeNormalizedParameter(r.Parameter) * s.Curve.Length;
                    list.Add((s.Start + along - half, s.Start + along + half));
                }
            }

            // faixas que passam do fim do perímetro continuam no começo
            var wrapped = new List<(double, double)>();
            foreach (var (from, to) in list)
            {
                wrapped.Add((from, to));
                if (from < 0) wrapped.Add((from + perimeter, perimeter));
                if (to > perimeter) wrapped.Add((0, to - perimeter));
            }
            return wrapped;
        }

        /// <summary>Posição livre mais próxima da desejada, procurando para os dois lados.</summary>
        private static double? Free(double desired, double perimeter, List<(double From, double To)> blocked, List<double> taken, double minGap)
        {
            double inc = RevitHelpers.M(0.05);
            for (double d = 0; d <= perimeter / 2; d += inc)
            {
                foreach (double sign in new[] { 1.0, -1.0 })
                {
                    double p = desired + sign * d;
                    p = ((p % perimeter) + perimeter) % perimeter;
                    if (blocked.Any(b => p >= b.From && p <= b.To)) continue;
                    if (taken.Any(t => Math.Min(Math.Abs(t - p), perimeter - Math.Abs(t - p)) < minGap)) continue;
                    return p;
                }
            }
            return null;
        }

        private static XYZ Tangent(Curve c, double normalized)
        {
            XYZ d = c.ComputeDerivatives(normalized, true).BasisX;
            return new XYZ(d.X, d.Y, 0).Normalize();
        }

        internal static bool Inside(RoomItem room, XYZ p) => room.Room switch
        {
            Room r => r.IsPointInRoom(p),
            Space s => s.IsPointInSpace(p),
            _ => false,
        };

        // ------------------------------------------------------------------ inserção

        private static FamilyInstance PlaceAt(Document doc, RoomItem room, List<Seg> segs, double position, FamilySymbol symbol,
            Level level, double heightM, HashSet<string> notes)
        {
            Seg seg = segs.Last(s => s.Start <= position + 1e-9);
            double local = Math.Max(0, Math.Min(1, (position - seg.Start) / seg.Curve.Length));
            XYZ onBoundary = seg.Curve.Evaluate(local, true);
            XYZ tangent = Tangent(seg.Curve, local);

            // normal para dentro do ambiente (teste a 10 cm da parede, 1 m acima do piso)
            var inward = new XYZ(-tangent.Y, tangent.X, 0);
            double roomFloor = room.Room.Level.ProjectElevation;
            XYZ probe = new XYZ(onBoundary.X, onBoundary.Y, roomFloor + RevitHelpers.M(1.0)) + inward * RevitHelpers.M(0.10);
            if (!Inside(room, probe)) inward = inward.Negate();

            XYZ pRoom = new XYZ(onBoundary.X, onBoundary.Y, roomFloor + RevitHelpers.M(heightM));
            return WallPlacement.Place(doc, seg.Wall, room.Link, room.T, pRoom, tangent, inward, symbol, level, heightM, notes);
        }
    }
}
