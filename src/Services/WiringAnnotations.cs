using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CircuitoLivre.Selection;

namespace CircuitoLivre.Services
{
    /// <summary>Quais circuitos aparecem nos identificadores de fiação de uma vista.</summary>
    public enum WiringVisibility { Todos, Iluminacao, IluminacaoEMisto, Forca, ForcaEMisto, Alimentador, NaoAlimentador }

    /// <summary>
    /// Identificadores próprios com textos e símbolos de condutores na vista:
    /// mostram os circuitos de cada trecho de eletroduto/eletrocalha, respeitando a visibilidade
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
            public Dictionary<long, List<long>> GraphicsByNote { get; set; } = new();
        }

        public class State
        {
            public Dictionary<string, ViewState> Views { get; set; } = new();
            /// <summary>Texto da fiação → número da legenda.</summary>
            public Dictionary<string, int> Legend { get; set; } = new();
            /// <summary>Trechos (UniqueId) mostrados como legenda.</summary>
            public List<string> LegendSegments { get; set; } = new();
            public Dictionary<string, int> CircuitNumbers { get; set; } = new();
        }

        private const string Key = "fiacao";
        private static readonly BuiltInCategory[] Infra = { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray };

        private readonly Document _doc;
        private readonly DimensioningSettings _cfg;
        private RoutingService _routing;
        private Dictionary<ElementId, ElectricalSystem> _circuits;
        private bool _numbersReady;
        private Dictionary<ElementId, HashSet<string>> _returns;

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
                string label = NumberFor(c);
                string line = string.IsNullOrWhiteSpace(wiring) ? label : $"{label}: {wiring.Trim()}";
                lines.Add(_cfg.SpaceTagsWithoutGround && !wiring.Contains("PE", StringComparison.OrdinalIgnoreCase)
                    ? line + "\r" : line);
            }
            if (maxPe > 0) lines.Add($"PE #{Nbr5410.FormatSection(maxPe)} mm² (único)");
            return string.Join("\r", lines);
        }

        private string NumberFor(ElectricalSystem circuit)
        {
            if (!_cfg.TagUniqueCircuitNumbers) return circuit.CircuitNumber;
            if (!_numbersReady)
            {
                Data.CircuitNumbers ??= new();
                WiringTagData.AssignNumbers(Data.CircuitNumbers, AllCircuits.OrderBy(c=>c.PanelName)
                    .ThenBy(c=>FirstNumber(c.CircuitNumber)).ThenBy(c=>c.UniqueId,StringComparer.Ordinal).Select(c=>c.UniqueId));
                _numbersReady = true;
            }
            return Data.CircuitNumbers[circuit.UniqueId].ToString();
        }

        private int ReturnsIn(ElementId segment, ElectricalSystem circuit)
        {
            if(_returns==null)
            {
                _returns=new();
                string ReturnOf(Element e)
                {
                    for(var current=e;current!=null;current=(current as FamilyInstance)?.SuperComponent)
                    {
                        string value=SharedParameters.GetText(current,SharedParameters.Return);
                        if(!string.IsNullOrWhiteSpace(value)) return value;
                    }
                    return "";
                }
                foreach(var c in AllCircuits)
                {
                    var devices=c.Elements.Cast<Element>().ToList();
                    var switches=devices.Where(e=>e.Category?.BuiltInCategory==BuiltInCategory.OST_LightingDevices).ToList();
                    var lights=devices.Where(e=>e.Category?.BuiltInCategory==BuiltInCategory.OST_LightingFixtures).ToList();
                    foreach(var sw in switches)
                    foreach(string letter in ReturnOf(sw).Split(new[]{',',' '},StringSplitOptions.RemoveEmptyEntries).Distinct())
                    foreach(var light in lights.Where(e=>ReturnOf(e).Split(new[]{',',' '},StringSplitOptions.RemoveEmptyEntries).Contains(letter)))
                    foreach(var id in Routing.PathBetween(sw,light))
                    {
                        if(!_returns.TryGetValue(id,out var names)) _returns[id]=names=new();
                        names.Add(c.UniqueId+":"+letter);
                    }
                }
            }
            return _returns.TryGetValue(segment,out var returns)?returns.Count(name=>name.StartsWith(circuit.UniqueId+":",StringComparison.Ordinal)):0;
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
            if (_cfg.TagWireSymbols) _ = WireSymbol(view);
            s.GraphicsByNote ??= new();
            foreach (long id in s.GraphicsByNote.Values.SelectMany(ids=>ids).Distinct())
                if (_doc.GetElement(new ElementId(id)) != null) _doc.Delete(new ElementId(id));
            s.GraphicsByNote.Clear();
            foreach (long id in s.Notes)
            {
                try { if (_doc.GetElement(new ElementId(id)) is TextNote or AnnotationSymbol) _doc.Delete(new ElementId(id)); } catch (Exception) { }
            }
            s.Notes.Clear();

            ElementId textType = TextType();
            double scale = Math.Max(1, view.Scale);
            double offset = 2.5 / 304.8 * scale;
            var legendSegs = new HashSet<string>(Data.LegendSegments);
            int count = 0;
            foreach (Element seg in SegmentsIn(view))
            {
                var circuits = CircuitsIn(seg.Id, s);
                string text = TextFor(circuits);
                if (text == null) continue;
                if (legendSegs.Contains(seg.UniqueId)) text = Symbol(LegendNumber(text));

                var line = (Line)((LocationCurve)seg.Location).Curve;
                XYZ mid = line.Evaluate(0.5, true);
                XYZ dir = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
                XYZ normal = XYZ.BasisZ.CrossProduct(dir);
                if (normal.DotProduct(view.UpDirection) < 0) normal = normal.Negate();
                XYZ at = new XYZ(mid.X, mid.Y, 0) + normal * offset;

                if (_cfg.TagWireSymbols && !legendSegs.Contains(seg.UniqueId))
                {
                    double mm = scale / 304.8;
                    XYZ axis = line.Direction - view.ViewDirection * line.Direction.DotProduct(view.ViewDirection);
                    if (axis.GetLength() < 1e-9) continue;
                    axis = axis.Normalize();
                    if (axis.DotProduct(view.RightDirection) < -1e-9) axis = axis.Negate();
                    XYZ up = view.ViewDirection.CrossProduct(axis).Normalize();
                    if (up.DotProduct(view.UpDirection) < 0) up = up.Negate();
                    XYZ center = mid - view.ViewDirection * (mid - view.Origin).DotProduct(view.ViewDirection);
                    bool graphics = circuits.All(c=>WiringTagData.Parse(SharedParameters.GetText(c,SharedParameters.Wiring)).Count>0);
                    if (graphics)
                    {
                        double sharedGround = 0;
                        XYZ drawingAxis=_cfg.TagAlongConduit?axis:view.RightDirection;
                        XYZ drawingUp=_cfg.TagAlongConduit?up:view.UpDirection;
                        XYZ baseline=center+up*(4*mm);
                        double cursor=0;
                        var group=new List<Element>();
                        void Add(string number,List<WiringTagData.Mark> marks)
                        {
                            var item=DrawSymbols(view,s,textType,baseline+drawingAxis*cursor,drawingAxis,drawingUp,mm,
                                number,marks,group.Count==0?center:null);
                            group.Add(item);
                            _doc.Regenerate();
                            var box=item.get_BoundingBox(view);
                            var size=box==null?XYZ.Zero:box.Max-box.Min;
                            double width=Math.Abs(size.X*drawingAxis.X)+Math.Abs(size.Y*drawingAxis.Y)+Math.Abs(size.Z*drawingAxis.Z);
                            cursor+=Math.Max(width,5*mm)+2*mm;
                        }
                        for (int row=0;row<circuits.Count;row++)
                        {
                            var c=circuits[row];
                            var marks=WiringTagData.Parse(SharedParameters.GetText(c,SharedParameters.Wiring));
                            double phase=marks.FirstOrDefault(m=>m.Role=="F")?.Section??0;
                            double returnSection = _cfg.SectionRule(phase)?.Return ?? phase;
                            for(int k=0;k<ReturnsIn(seg.Id,c)&&returnSection>0;k++) marks.Add(new("R",returnSection));
                            if (_cfg.GroundGrouping==GroundGrouping.OnePerRun ||
                                (_cfg.GroundGrouping==GroundGrouping.LightingOnly && KindOf(c)==Kind.Lighting))
                            {
                                sharedGround=Math.Max(sharedGround,marks.Where(m=>m.Role=="T").Select(m=>m.Section).DefaultIfEmpty(0).Max());
                                marks.RemoveAll(m=>m.Role=="T");
                            }
                            Add(NumberFor(c),marks);
                            count++;
                        }
                        if (sharedGround>0) Add("T",new List<WiringTagData.Mark>{new("T",sharedGround)});
                        long root=group[0].Id.Value;
                        foreach(var item in group.Skip(1))
                        {
                            long id=item.Id.Value;
                            s.Notes.Remove(id);
                            if(!s.GraphicsByNote.TryGetValue(root,out var children)) s.GraphicsByNote[root]=children=new();
                            children.Add(id);
                            if(s.GraphicsByNote.Remove(id,out var extras)) children.AddRange(extras);
                        }
                        if(group.Count>1 && Math.Abs(line.Direction.DotProduct(drawingAxis))>0.7)
                        {
                            var boxes=group.Select(e=>e.get_BoundingBox(view)).Where(b=>b!=null).ToList();
                            if(boxes.Count==0) continue;
                            double Middle(BoundingBoxXYZ b)=>((b.Min+b.Max)*0.5).DotProduct(drawingAxis);
                            double Half(BoundingBoxXYZ b)=>0.5*(Math.Abs((b.Max.X-b.Min.X)*drawingAxis.X)+
                                Math.Abs((b.Max.Y-b.Min.Y)*drawingAxis.Y)+Math.Abs((b.Max.Z-b.Min.Z)*drawingAxis.Z));
                            double middle=(boxes.Min(b=>Middle(b)-Half(b))+boxes.Max(b=>Middle(b)+Half(b)))*0.5;
                            var ids=group.Select(e=>e.Id).Concat(s.GraphicsByNote[root].Select(id=>new ElementId(id))).Distinct().ToList();
                            var links=ids.Select(id=>_doc.GetElement(id)).OfType<DetailCurve>().ToList();
                            ElementTransformUtils.MoveElements(_doc,ids,drawingAxis*(baseline.DotProduct(drawingAxis)-middle));
                            foreach(var link in links)
                            {
                                var location=(LocationCurve)link.Location;
                                location.Curve=Line.CreateBound(center,location.Curve.GetEndPoint(1));
                            }
                        }
                        continue;
                    }
                }

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
            if (_cfg.TagWireSymbols) Adjust(view);
            return count;
        }

        private FamilySymbol _wireSymbol;

        private FamilySymbol WireSymbol(View view)
        {
            if (_wireSymbol != null) return _wireSymbol;
            const string familyName = "CL_SMB_Fiação";
            bool RepresentsZero(FamilySymbol type)
            {
                using var probe=new SubTransaction(_doc);
                probe.Start();
                try
                {
                    if(!type.IsActive) type.Activate();
                    var instance=_doc.Create.NewFamilyInstance(view.Origin,type,view);
                    instance.LookupParameter("N_Retorno").Set(0);
                    _doc.Regenerate();
                    return instance.LookupParameter("N_Retorno").AsInteger()==0;
                }
                catch(System.Exception) { return false; }
                finally { probe.RollBack(); }
            }
            string[] counts={"N_Fase","N_Neutro","N_Terra","N_Retorno"};
            string[] texts={"N_Circuito","Bit_Fase","Bit_Terra"};
            _wireSymbol = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(s=>s.Category?.BuiltInCategory==BuiltInCategory.OST_GenericAnnotation &&
                    counts.All(name=>s.LookupParameter(name) is Parameter p && !p.IsReadOnly && p.StorageType==StorageType.Integer) &&
                    texts.All(name=>s.LookupParameter(name) is Parameter p && !p.IsReadOnly && p.StorageType==StorageType.String))
                .GroupBy(s=>s.Symbol.Id).OrderByDescending(g=>g.Count()).ThenBy(g=>g.First().Symbol.FamilyName,StringComparer.Ordinal)
                .Select(g=>g.First().Symbol).FirstOrDefault(RepresentsZero);
            _wireSymbol ??= new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == familyName && s.Category?.BuiltInCategory == BuiltInCategory.OST_GenericAnnotation);
            if (_wireSymbol == null)
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CircuitoLivre", "Familias", "0.3.15");
                System.IO.Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, familyName + ".rfa");
                using (var resource = typeof(WiringAnnotations).Assembly.GetManifestResourceStream("CircuitoLivre.Resources.WiringAnnotation.rfa"))
                {
                    if (resource == null) throw new InvalidOperationException("Família de fiação ausente no pacote.");
                    using var file = System.IO.File.Create(path);
                    resource.CopyTo(file);
                }
                if (!_doc.LoadFamily(path, out Family family)) throw new InvalidOperationException("Não foi possível carregar a família de fiação.");
                _wireSymbol = family.GetFamilySymbolIds().Select(id => _doc.GetElement(id)).OfType<FamilySymbol>().First();
            }
            if (!_wireSymbol.IsActive) { _wireSymbol.Activate(); _doc.Regenerate(); }
            return _wireSymbol;
        }

        private Element DrawSymbols(View view, ViewState state, ElementId textType, XYZ center, XYZ axis, XYZ up,
            double mm, string number, List<WiringTagData.Mark> marks, XYZ anchor=null)
        {
            TextNote DrawText()
            {
                string text=number+": "+string.Join(" + ",marks.GroupBy(m=>new{m.Role,m.Section})
                    .Select(g=>(g.Count()>1?g.Count().ToString():"")+g.Key.Role+
                        (_cfg.TagShowSection?" #"+Nbr5410.FormatSection(g.Key.Section):"")));
                var note=TextNote.Create(_doc,view.Id,center,text,new TextNoteOptions(textType));
                state.Notes.Add(note.Id.Value);
                return note;
            }
            if(marks.Count==0||marks.Count(m=>m.Role=="F")>3||marks.Count(m=>m.Role=="N")>1||marks.Count(m=>m.Role=="T")>1)
            { return DrawText(); }
            var symbol = _doc.Create.NewFamilyInstance(center, WireSymbol(view), view);
            void Count(string name, string role)
            {
                var p = symbol.LookupParameter(name);
                if (p == null || p.IsReadOnly || !p.Set(marks.Count(m=>m.Role==role)))
                    throw new InvalidOperationException("Parâmetro de fiação incompatível: " + name);
            }
            void Text(string name, string value)
            {
                var p = symbol.LookupParameter(name);
                if (p == null || p.IsReadOnly || !p.Set(value))
                    throw new InvalidOperationException("Parâmetro de fiação incompatível: " + name);
            }
            string Section(IEnumerable<WiringTagData.Mark> conductors)
            {
                var groups=conductors.GroupBy(m=>m.Section).ToList();
                return !_cfg.TagShowSection ? "" : string.Join(" / ",groups
                    .Where(g=>Math.Abs(g.Key-_cfg.OmitSectionInTags)>1e-9)
                    .Select(g=>(groups.Count>1?string.Join("",g.Select(m=>m.Role).Distinct())+" ":"")+Nbr5410.FormatSection(g.Key)));
            }
            Count("N_Fase","F"); Count("N_Neutro","N"); Count("N_Terra","T"); Count("N_Retorno","R");
            Text("N_Circuito",number);
            Text("Bit_Fase",Section(marks.Where(m=>m.Role!="T")));
            var phaseSections=marks.Where(m=>m.Role!="T").Select(m=>m.Section).ToHashSet();
            Text("Bit_Terra",marks.Where(m=>m.Role=="T").All(m=>phaseSections.Contains(m.Section))?"":Section(marks.Where(m=>m.Role=="T")));
            symbol.LookupParameter("Linha Central")?.Set(1);
            _doc.Regenerate();
            if(symbol.LookupParameter("Aviso") is Parameter valid && valid.StorageType==StorageType.Integer && valid.AsInteger()==0)
            {
                _doc.Delete(symbol.Id);
                return DrawText();
            }
            if (_cfg.TagAlongConduit)
                ElementTransformUtils.RotateElement(_doc,symbol.Id,Line.CreateUnbound(center,view.ViewDirection),
                    view.RightDirection.AngleOnPlaneTo(axis,view.ViewDirection));
            state.Notes.Add(symbol.Id.Value);
            var graphics=new List<long>();
            if (anchor!=null)
            {
                var connector=_doc.Create.NewDetailCurve(view,Line.CreateBound(anchor,center+axis*mm));
                var lines=_doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                var style=lines.SubCategories.Cast<Category>().FirstOrDefault(c=>c.Name=="CL Chamada de fiação");
                if(style==null)
                {
                    style=_doc.Settings.Categories.NewSubcategory(lines,"CL Chamada de fiação");
                    style.LineColor=new Color(0,0,0);style.SetLineWeight(1,GraphicsStyleType.Projection);
                }
                connector.LineStyle=style.GetGraphicsStyle(GraphicsStyleType.Projection);
                graphics.Add(connector.Id.Value);
            }
            state.GraphicsByNote[symbol.Id.Value]=graphics;
            return symbol;
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
            List<Element> notes = s.Notes.Select(id => _doc.GetElement(new ElementId(id))).Where(n => n is TextNote or AnnotationSymbol).ToList();
            double step = 3.0 / 304.8 * Math.Max(1, view.Scale);
            int moved = 0;
            var owned=s.Notes.Concat(s.GraphicsByNote?.Values.SelectMany(ids=>ids)??Enumerable.Empty<long>()).ToHashSet();
            var placed = new FilteredElementCollector(_doc,view.Id).WhereElementIsNotElementType()
                .Where(e=>!owned.Contains(e.Id.Value) && (e is TextNote or AnnotationSymbol or IndependentTag ||
                    e is FamilyInstance && e.Category?.BuiltInCategory is BuiltInCategory.OST_ElectricalEquipment or
                        BuiltInCategory.OST_ElectricalFixtures or BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_LightingDevices))
                .Select(e=>e.get_BoundingBox(view)).Where(b=>b!=null).ToList();
            foreach (Element n in notes)
            {
                var ids=new List<ElementId>{n.Id};
                if(s.GraphicsByNote!=null && s.GraphicsByNote.TryGetValue(n.Id.Value,out var graphics))
                    ids.AddRange(graphics.Select(id=>new ElementId(id)).Where(id=>_doc.GetElement(id)!=null));
                var visuals=ids.Select(id=>_doc.GetElement(id)).Where(e=>e is TextNote or AnnotationSymbol).ToList();
                XYZ previous=XYZ.Zero;
                XYZ[] positions={view.RightDirection*step,-view.RightDirection*step,
                    view.RightDirection*step*2,-view.RightDirection*step*2,
                    view.UpDirection*step,-view.UpDirection*step,
                    view.UpDirection*step*2,-view.UpDirection*step*2,
                    (view.RightDirection+view.UpDirection)*step,(-view.RightDirection+view.UpDirection)*step,
                    (view.RightDirection-view.UpDirection)*step,(-view.RightDirection-view.UpDirection)*step};
                for (int attempt = 0; attempt <= positions.Length; attempt++)
                {
                    _doc.Regenerate();
                    var boxes=visuals.Select(e=>e.get_BoundingBox(view)).Where(b=>b!=null).ToList();
                    BoundingBoxXYZ box = boxes.Count==0?null:new BoundingBoxXYZ {
                        Min=new XYZ(boxes.Min(b=>b.Min.X),boxes.Min(b=>b.Min.Y),boxes.Min(b=>b.Min.Z)),
                        Max=new XYZ(boxes.Max(b=>b.Max.X),boxes.Max(b=>b.Max.Y),boxes.Max(b=>b.Max.Z)) };
                    if (box == null || !placed.Any(b => Overlaps(b, box))) { if (box != null) placed.Add(box); break; }
                    if(attempt==positions.Length) { placed.Add(box);break; }
                    XYZ delta=positions[attempt]-previous;
                    previous=positions[attempt];
                    var ends=(n as TextNote)?.GetLeaders().Select(l=>l.End).ToList();
                    var anchors=n is AnnotationSymbol ? ids.Select(id=>_doc.GetElement(id)).OfType<DetailCurve>()
                        .ToDictionary(c=>c.Id,c=>c.GeometryCurve.GetEndPoint(0)) : new Dictionary<ElementId,XYZ>();
                    ElementTransformUtils.MoveElements(_doc, ids, delta);
                    var leaders=(n as TextNote)?.GetLeaders();
                    for(int i=0;i<(ends?.Count??0);i++) leaders[i].End=ends[i];
                    foreach(var pair in anchors)
                    {
                        var location=(LocationCurve)_doc.GetElement(pair.Key).Location;
                        location.Curve=Line.CreateBound(pair.Value,location.Curve.GetEndPoint(1));
                    }
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
