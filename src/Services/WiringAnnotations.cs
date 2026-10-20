using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RafaelElectrical.Selection;

namespace RafaelElectrical.Services
{
    /// <summary>Quais circuitos aparecem nos identificadores de fiação de uma vista.</summary>
    public enum WiringVisibility { Todos, Iluminacao, IluminacaoEMisto, Forca, ForcaEMisto, Alimentador, NaoAlimentador }

    /// <summary>
    /// Identificadores de fiação feitos pelo plugin como textos na vista (sem família de identificador):
    /// um texto por trecho de eletroduto/eletrocalha com os circuitos que passam, respeitando a visibilidade
    /// configurada por vista e a substituição por legenda numerada. O estado fica guardado no projeto.
    /// </summary>
    public class WiringAnnotations
    {
        public class ViewState
        {
            public WiringVisibility Mode { get; set; } = WiringVisibility.Todos;
            /// <summary>Circuitos escolhidos um a um (null = todos os do modo).</summary>
            public List<long> Circuits { get; set; }
            public List<long> Notes { get; set; } = new();
        }

        public class State
        {
            public Dictionary<string, ViewState> Views { get; set; } = new();
            /// <summary>Texto da fiação → número da legenda.</summary>
            public Dictionary<string, int> Legend { get; set; } = new();
            /// <summary>Trechos (UniqueId) mostrados como legenda.</summary>
            public List<string> LegendSegments { get; set; } = new();
        }

        private const string Key = "fiacao";
        private static readonly BuiltInCategory[] Infra = { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray };

        private readonly Document _doc;
        private readonly DimensioningSettings _cfg;
        private RoutingService _routing;
        private Dictionary<ElementId, ElectricalSystem> _circuits;

        public State Data { get; }

        public WiringAnnotations(Document doc)
        {
            _doc = doc;
            _cfg = DimensioningSettings.Load();
            Data = Load(doc);
        }

        public static State Load(Document doc)
        {
            string json = ProjectStorage.GetExtra(doc, Key);
            if (json == null) return new State();
            try { return JsonSerializer.Deserialize<State>(json) ?? new State(); }
            catch (Exception) { return new State(); }
        }

        /// <summary>Grava o estado no projeto (dentro de uma transação).</summary>
        public void Save() => ProjectStorage.SetExtra(_doc, Key, JsonSerializer.Serialize(Data));

        public ViewState For(View view)
        {
            string k = view.Id.Value.ToString();
            if (!Data.Views.TryGetValue(k, out ViewState s)) Data.Views[k] = s = new ViewState();
            return s;
        }

        public static string ModeLabel(WiringVisibility m) => m switch
        {
            WiringVisibility.Iluminacao => "Iluminação",
            WiringVisibility.IluminacaoEMisto => "Iluminação e misto",
            WiringVisibility.Forca => "Força",
            WiringVisibility.ForcaEMisto => "Força e misto",
            WiringVisibility.Alimentador => "Alimentador",
            WiringVisibility.NaoAlimentador => "Não alimentador",
            _ => "Todos",
        };

        // ------------------------------------------------------------------ roteamento e classificação

        public RoutingService Routing
        {
            get
            {
                if (_routing != null) return _routing;
                _circuits = new FilteredElementCollector(_doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                    .Where(c => c.BaseEquipment != null).ToDictionary(c => c.Id);
                _routing = new RoutingService(_doc);
                _routing.RouteCircuits(_circuits.Values);
                return _routing;
            }
        }

        public IEnumerable<ElectricalSystem> AllCircuits
        {
            get { _ = Routing; return _circuits.Values; }
        }

        private enum Kind { Lighting, Power, Mixed, Feeder }

        private Kind KindOf(ElectricalSystem c)
        {
            List<Element> devices = c.Elements.Cast<Element>().Select(e => _doc.GetElement(e.Id)).Where(e => e != null).ToList();
            if (devices.Any(CircuitDeviceFilter.IsPanel)) return Kind.Feeder;
            bool light = devices.Any(d => d.Category?.BuiltInCategory is BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_LightingDevices);
            bool power = devices.Any(d => d.Category?.BuiltInCategory is not (BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_LightingDevices));
            if (light && power) return Kind.Mixed;
            return light ? Kind.Lighting : Kind.Power;
        }

        private bool Visible(ElectricalSystem c, ViewState s)
        {
            if (s.Circuits != null && !s.Circuits.Contains(c.Id.Value)) return false;
            Kind k = KindOf(c);
            return s.Mode switch
            {
                WiringVisibility.Iluminacao => k == Kind.Lighting,
                WiringVisibility.IluminacaoEMisto => k is Kind.Lighting or Kind.Mixed,
                WiringVisibility.Forca => k == Kind.Power,
                WiringVisibility.ForcaEMisto => k is Kind.Power or Kind.Mixed,
                WiringVisibility.Alimentador => k == Kind.Feeder,
                WiringVisibility.NaoAlimentador => k != Kind.Feeder,
                _ => true,
            };
        }

        // ------------------------------------------------------------------ textos

        /// <summary>Circuitos visíveis que passam pelo trecho, já filtrados pela vista.</summary>
        public List<ElectricalSystem> CircuitsIn(ElementId segment, ViewState s)
        {
            if (!Routing.CircuitsBySegment.TryGetValue(segment, out var ids)) return new List<ElectricalSystem>();
            return ids.Select(id => _circuits.TryGetValue(id, out var c) ? c : null).Where(c => c != null && Visible(c, s))
                .OrderBy(c => c.PanelName).ThenBy(c => FirstNumber(c.CircuitNumber)).ToList();
        }

        private static int FirstNumber(string s) =>
            int.TryParse(new string((s ?? "").TakeWhile(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue;

        private static readonly Regex PeRegex = new Regex(@"\s*\+\s*PE\s*(#\s*[\d,\.]+\s*(mm²)?)?", RegexOptions.IgnoreCase);
        private static readonly Regex SectionRegex = new Regex(@"\s*#\s*[\d,\.]+\s*(mm²)?", RegexOptions.IgnoreCase);

        public string TextFor(List<ElectricalSystem> circuits)
        {
            if (circuits.Count == 0) return null;
            double omit = _cfg.OmitSectionInTags;
            string omitText = omit > 0 ? $"#{Nbr5410.FormatSection(omit)}" : null;
            bool groupGround = _cfg.GroundGrouping == GroundGrouping.OnePerRun;
            double maxPe = 0;
            var lines = new List<string>();
            foreach (ElectricalSystem c in circuits)
            {
                string wiring = SharedParameters.GetText(c, SharedParameters.Wiring) ?? "";
                bool lighting = KindOf(c) == Kind.Lighting;
                if (groupGround || (_cfg.GroundGrouping == GroundGrouping.LightingOnly && lighting))
                {
                    maxPe = Math.Max(maxPe, SharedParameters.GetNumber(c, SharedParameters.ProtectionSection) ?? 0);
                    wiring = PeRegex.Replace(wiring, "");
                }
                if (!_cfg.TagShowSection) wiring = SectionRegex.Replace(wiring, "");
                else if (omitText != null && wiring.Contains(omitText) && wiring.IndexOf('#') == wiring.LastIndexOf('#'))
                    wiring = SectionRegex.Replace(wiring, "");
                string label = $"{c.PanelName}-{c.CircuitNumber}";
                string line = string.IsNullOrWhiteSpace(wiring) ? label : $"{label}: {wiring.Trim()}";
                lines.Add(_cfg.SpaceTagsWithoutGround && !wiring.Contains("PE", StringComparison.OrdinalIgnoreCase)
                    ? line + "\r" : line);
            }
            if (maxPe > 0) lines.Add($"PE #{Nbr5410.FormatSection(maxPe)} mm² (único)");
            return string.Join("\r", lines);
        }

        public string Symbol(int n)
        {
            if (!_cfg.LegendAlphabetic) return n.ToString();
            string s = "";
            for (int i = n; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
            return s;
        }

        public int LegendNumber(string text)
        {
            if (Data.Legend.TryGetValue(text, out int n)) return n;
            n = Data.Legend.Count == 0 ? 1 : Data.Legend.Values.Max() + 1;
            Data.Legend[text] = n;
            return n;
        }

        // ------------------------------------------------------------------ vistas

        public List<Element> SegmentsIn(View view) =>
            new FilteredElementCollector(_doc, view.Id).WherePasses(new ElementMulticategoryFilter(Infra))
                .WhereElementIsNotElementType()
                .Where(e => (e.Location as LocationCurve)?.Curve is Line l && l.Length > RevitHelpers.M(0.30) && Math.Abs(l.Direction.Z) < 0.7)
                .ToList();

        /// <summary>Apaga os textos antigos da vista e cria os novos. Precisa de transação.</summary>
        public int Generate(View view)
        {
            ViewState s = For(view);
            foreach (long id in s.Notes)
            {
                try { if (_doc.GetElement(new ElementId(id)) is TextNote) _doc.Delete(new ElementId(id)); } catch (Exception) { }
            }
            s.Notes.Clear();

            ElementId textType = TextType();
            double scale = Math.Max(1, view.Scale);
            double offset = 2.5 / 304.8 * scale;
            var legendSegs = new HashSet<string>(Data.LegendSegments);
            int count = 0;
            foreach (Element seg in SegmentsIn(view))
            {
                string text = TextFor(CircuitsIn(seg.Id, s));
                if (text == null) continue;
                if (legendSegs.Contains(seg.UniqueId)) text = Symbol(LegendNumber(text));

                var line = (Line)((LocationCurve)seg.Location).Curve;
                XYZ mid = line.Evaluate(0.5, true);
                XYZ dir = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
                XYZ normal = XYZ.BasisZ.CrossProduct(dir);
                if (normal.DotProduct(view.UpDirection) < 0) normal = normal.Negate();
                XYZ at = new XYZ(mid.X, mid.Y, 0) + normal * offset;

                var options = new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center };
                if (_cfg.TagAlongConduit)
                {
                    double angle = view.RightDirection.AngleOnPlaneTo(dir, view.ViewDirection);
                    if (angle > Math.PI / 2 && angle <= 3 * Math.PI / 2) angle -= Math.PI;
                    options.Rotation = angle;
                }
                try
                {
                    TextNote note = TextNote.Create(_doc, view.Id, at, text, options);
                    s.Notes.Add(note.Id.Value);
                    count++;
                }
                catch (Exception) { }
            }
            return count;
        }

        /// <summary>Tipo de texto "RE Fiação" no tamanho das configurações.</summary>
        private ElementId TextType()
        {
            string name = $"RE Fiação {_cfg.TagTextMm:0.0} mm";
            TextNoteType existing = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault(t => t.Name == name);
            if (existing != null) return existing.Id;
            var baseType = _doc.GetElement(_doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
            if (baseType == null) return _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            try
            {
                var t = (TextNoteType)baseType.Duplicate(name);
                t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(_cfg.TagTextMm / 304.8);
                return t.Id;
            }
            catch (Exception) { return baseType.Id; }
        }

        /// <summary>Afasta textos sobrepostos deslocando-os ao longo do próprio trecho. Precisa de transação.</summary>
        public int Adjust(View view)
        {
            ViewState s = For(view);
            List<TextNote> notes = s.Notes.Select(id => _doc.GetElement(new ElementId(id)) as TextNote).Where(n => n != null).ToList();
            double step = 3.0 / 304.8 * Math.Max(1, view.Scale);
            int moved = 0;
            var placed = new List<BoundingBoxXYZ>();
            foreach (TextNote n in notes)
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    _doc.Regenerate();
                    BoundingBoxXYZ box = n.get_BoundingBox(view);
                    if (box == null || !placed.Any(b => Overlaps(b, box))) { if (box != null) placed.Add(box); break; }
                    XYZ delta = (attempt % 2 == 0 ? view.UpDirection : view.RightDirection) * step * (1 + attempt / 2);
                    ElementTransformUtils.MoveElement(_doc, n.Id, delta);
                    moved++;
                }
            }
            return moved;
        }

        private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X < b.Max.X && b.Min.X < a.Max.X && a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;

        /// <summary>Vista de desenho com o significado de cada legenda. Precisa de transação.</summary>
        public View BuildLegendView()
        {
            const string name = "RE - Legenda de fiação";
            View view = new FilteredElementCollector(_doc).OfClass(typeof(ViewDrafting)).Cast<View>().FirstOrDefault(v => v.Name == name);
            if (view == null)
            {
                ViewFamilyType vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
                if (vft == null) return null;
                view = ViewDrafting.Create(_doc, vft.Id);
                try { view.Name = name; } catch (Exception) { }
            }
            foreach (ElementId id in new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType()
                         .Where(e => e.OwnerViewId == view.Id).Select(e => e.Id).ToList())
            {
                try { _doc.Delete(id); } catch (Exception) { }
            }

            ElementId textType = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            double mm = Math.Max(1, view.Scale) / 304.8;
            TextNote.Create(_doc, view.Id, XYZ.Zero, "LEGENDA DE FIAÇÃO", textType);
            double y = -10 * mm;
            foreach (var kv in Data.Legend.OrderBy(k => k.Value))
            {
                TextNote.Create(_doc, view.Id, new XYZ(0, y, 0), Symbol(kv.Value), textType);
                TextNote note = TextNote.Create(_doc, view.Id, new XYZ(12 * mm, y, 0), kv.Key, textType);
                y -= (6 + 4.5 * kv.Key.Count(ch => ch == '\r')) * mm;
            }
            return view;
        }
    }
}
