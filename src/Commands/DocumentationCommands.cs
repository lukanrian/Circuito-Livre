using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Microsoft.Win32;
using RafaelElectrical.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    // =====================================================================================
    //  Criar identificadores (tags)
    // =====================================================================================

    /// <summary>
    /// Cria identificadores na vista ativa usando a família de identificador padrão de cada categoria:
    /// dispositivos selecionados, todos os dispositivos elétricos da vista ou a infraestrutura (fiação).
    /// Elementos que já têm identificador na vista são ignorados.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateTagsCommand : IExternalCommand
    {
        private static readonly BuiltInCategory[] DeviceCategories =
        {
            BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_NurseCallDevices,
            BuiltInCategory.OST_ElectricalEquipment,
        };

        private static readonly BuiltInCategory[] InfraCategories = { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            if (view is not ViewPlan && view is not ViewSection)
            {
                TaskDialog.Show("Criar identificadores", "Abra uma planta ou corte.");
                return Result.Cancelled;
            }

            var dialog = new TaskDialog("Criar identificadores") { MainInstruction = "Identificar o quê?", CommonButtons = TaskDialogCommonButtons.Cancel };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Elementos que eu selecionar");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Todos os dispositivos e quadros da vista");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Eletrodutos e eletrocalhas da vista (fiação)",
                "Use uma família de identificador de eletroduto que mostre o parâmetro RE_Cabos ou RE_Circuitos.");

            List<Element> targets;
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    try
                    {
                        targets = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).ToList();
                        if (targets.Count == 0)
                            targets = uidoc.Selection.PickObjects(ObjectType.Element, "Selecione os elementos e clique em Concluir")
                                .Select(r => doc.GetElement(r)).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                    break;
                case TaskDialogResult.CommandLink2:
                    targets = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(DeviceCategories))
                        .WhereElementIsNotElementType().ToElements().ToList();
                    break;
                case TaskDialogResult.CommandLink3:
                    targets = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(InfraCategories))
                        .WhereElementIsNotElementType().ToElements().ToList();
                    break;
                default:
                    return Result.Cancelled;
            }

            var alreadyTagged = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>().SelectMany(t => t.GetTaggedLocalElementIds()));

            int created = 0;
            var failedCategories = new HashSet<string>();
            double off = RevitHelpers.M(0.25) * Math.Max(1, view.Scale / 50.0);

            using (var t = new Transaction(doc, "Criar identificadores"))
            {
                t.Start();
                foreach (Element e in targets.Where(e => e != null && !alreadyTagged.Contains(e.Id) && e.Category != null))
                {
                    XYZ p = RevitHelpers.PointOf(e);
                    if (p == null) continue;
                    try
                    {
                        IndependentTag.Create(doc, view.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY,
                            TagOrientation.Horizontal, p + new XYZ(off, off, 0));
                        created++;
                    }
                    catch (Exception) { failedCategories.Add(e.Category.Name); }
                }
                t.Commit();
            }

            TaskDialog.Show("Criar identificadores", $"{created} identificador(es) criado(s)." +
                (failedCategories.Count > 0
                    ? "\n\nSem família de identificador carregada (ou não identificável) para: " + string.Join(", ", failedCategories) +
                      ". Carregue a família de identificador dessas categorias no projeto."
                    : ""));
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Relatório de fiação
    // =====================================================================================

    /// <summary>
    /// Quantitativo de cabos por quadro e por seção (fase, neutro, proteção), usando o comprimento real do
    /// roteamento (ou o do Revit) e uma folga percentual. Exporta CSV para Excel.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class WiringReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            DimensioningSettings cfg = DimensioningSettings.Load();
            List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>().Where(c => c.BaseEquipment != null).ToList();
            if (circuits.Count == 0)
            {
                TaskDialog.Show("Relatório de fiação", "Nenhum circuito ligado a quadro.");
                return Result.Cancelled;
            }

            double defaultExtra = Math.Round((cfg.WiringFactor - 1) * 100, 1);
            var window = new ChecklistWindow("Relatório de fiação",
                    "Força: circuitos sem seção dimensionada são contados como 2,5 mm². Telecom: tipo de cabo definido em Configurações.",
                    new List<CheckItem>(), "Exportar").AllowEmpty().WithText("Folga (%):", defaultExtra.ToString(CultureInfo.InvariantCulture));
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;
            double extra = double.TryParse(window.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double e) ? e : defaultExtra;

            // (quadro, função, cabo) -> metros
            var totals = new SortedDictionary<(string Panel, string Role, string Cable), double>();
            var colors = new Dictionary<string, string>
            {
                ["Fase"] = $"{cfg.ColorPhaseA}/{cfg.ColorPhaseB}/{cfg.ColorPhaseC}",
                ["Neutro"] = cfg.ColorNeutral,
                ["Proteção"] = cfg.ColorGround,
                ["Retorno"] = cfg.ColorReturn,
            };
            int noLength = 0;
            foreach (ElectricalSystem c in circuits)
            {
                double length = SharedParameters.GetNumber(c, SharedParameters.RealLength)
                                ?? RevitHelpers.ToM(c.Length);
                if (length <= 0) { noLength++; continue; }
                length *= 1 + extra / 100;
                string panel = c.PanelName;

                void Add(string role, string cable, int n)
                {
                    var key = (panel, role, cable);
                    totals[key] = (totals.TryGetValue(key, out double v) ? v : 0) + length * n;
                }

                switch (c.SystemType)
                {
                    case ElectricalSystemType.PowerCircuit:
                    {
                        double phase = SharedParameters.GetNumber(c, SharedParameters.Section) ?? 2.5;
                        double neutral = SharedParameters.GetNumber(c, SharedParameters.NeutralSection) ?? phase;
                        double pe = SharedParameters.GetNumber(c, SharedParameters.ProtectionSection) ?? phase;
                        int poles = Math.Max(1, c.PolesNumber);
                        string Mm(double x) => $"{Nbr5410.FormatSection(x)} mm²";
                        Add("Fase", Mm(phase), poles);
                        if (poles != 2) Add("Neutro", Mm(neutral), 1);
                        Add("Proteção", Mm(pe), 1);
                        break;
                    }
                    case ElectricalSystemType.Data: Add("Dados", cfg.CableData, 1); break;
                    case ElectricalSystemType.Telephone: Add("Telefone", cfg.CableTelephone, 1); break;
                    case ElectricalSystemType.Communication: Add("Comunicação", cfg.CableCommunication, 1); break;
                    case ElectricalSystemType.Security: Add("Segurança", cfg.CableSecurity, 1); break;
                    default: Add(c.SystemType.ToString(), "cabo", 1); break;
                }
            }

            var dialog = new SaveFileDialog { Filter = "CSV (Excel) (*.csv)|*.csv", FileName = "Relatorio_Fiacao.csv", Title = "Salvar relatório de fiação" };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            string Color(string role) => colors.TryGetValue(role, out string v) ? v : "";
            var sb = new StringBuilder();
            sb.AppendLine($"Relatório de fiação;folga {extra.ToString(br)} %");
            sb.AppendLine("Quadro;Função;Cabo;Cor;Comprimento (m)");
            foreach (var kv in totals)
                sb.AppendLine($"{kv.Key.Panel};{kv.Key.Role};{kv.Key.Cable};{Color(kv.Key.Role)};{kv.Value.ToString("0.0", br)}");
            sb.AppendLine();
            sb.AppendLine("TOTAL GERAL;Função;Cabo;Cor;Comprimento (m)");
            foreach (var g in totals.GroupBy(kv => (kv.Key.Role, kv.Key.Cable)).OrderBy(g => g.Key.Role).ThenBy(g => g.Key.Cable))
                sb.AppendLine($";{g.Key.Role};{g.Key.Cable};{Color(g.Key.Role)};{g.Sum(x => x.Value).ToString("0.0", br)}");

            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            TaskDialog.Show("Relatório de fiação", "Relatório exportado." +
                (noLength > 0 ? $"\n{noLength} circuito(s) sem comprimento ficaram de fora (rode o Atualizar Quadros)." : ""));
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Tabelas de fiação (schedules nativas)
    // =====================================================================================

    /// <summary>Cria a tabela (schedule) de circuitos com fiação, seção, disjuntor e comprimento, agrupada por quadro ou por nível.</summary>
    [Transaction(TransactionMode.Manual)]
    public class WiringScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            var dialog = new TaskDialog("Tabelas de fiação") { MainInstruction = "Agrupar a tabela por:", CommonButtons = TaskDialogCommonButtons.Cancel };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Quadro");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Nível (usa RE_Nível — rode antes o Preencher Identificação)");
            TaskDialogResult choice = dialog.Show();
            if (choice != TaskDialogResult.CommandLink1 && choice != TaskDialogResult.CommandLink2) return Result.Cancelled;
            bool byLevel = choice == TaskDialogResult.CommandLink2;

            ViewSchedule schedule;
            using (var t = new Transaction(doc, "Criar tabela de fiação"))
            {
                t.Start();
                SharedParameters.EnsureRouting(doc);
                SharedParameters.EnsureIdentification(doc);
                doc.Regenerate();

                schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_ElectricalCircuit));
                string baseName = byLevel ? "RE - Fiação por nível" : "RE - Fiação por quadro";
                string name = baseName;
                for (int i = 2; new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Any(v => v.Name == name); i++) name = $"{baseName} ({i})";
                schedule.Name = name;

                ScheduleDefinition def = schedule.Definition;
                IList<SchedulableField> fields = def.GetSchedulableFields();

                ScheduleField Add(ElementId paramId)
                {
                    SchedulableField f = fields.FirstOrDefault(x => x.ParameterId == paramId);
                    return f == null ? null : def.AddField(f);
                }
                ElementId Shared(ParamDef d) => SharedParameterElement.Lookup(doc, d.Guid)?.Id ?? ElementId.InvalidElementId;

                ScheduleField group = byLevel ? Add(Shared(SharedParameters.LevelDef)) : Add(new ElementId(BuiltInParameter.RBS_ELEC_CIRCUIT_PANEL_PARAM));
                if (byLevel) Add(new ElementId(BuiltInParameter.RBS_ELEC_CIRCUIT_PANEL_PARAM));
                ScheduleField number = Add(new ElementId(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER));
                Add(new ElementId(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME));
                Add(Shared(SharedParameters.Wiring));
                Add(Shared(SharedParameters.Section));
                Add(Shared(SharedParameters.ProtectionSection));
                Add(Shared(SharedParameters.Breaker));
                Add(Shared(SharedParameters.RealLength));
                Add(Shared(SharedParameters.VoltageDrop));

                if (group != null) def.AddSortGroupField(new ScheduleSortGroupField(group.FieldId) { ShowHeader = true, ShowFooter = true });
                if (number != null) def.AddSortGroupField(new ScheduleSortGroupField(number.FieldId));
                def.IsItemized = true;
                t.Commit();
            }

            data.Application.ActiveUIDocument.ActiveView = schedule;
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Diagrama unifilar
    // =====================================================================================

    /// <summary>Gera o diagrama unifilar de cada quadro selecionado numa vista de desenho (linhas e textos).</summary>
    [Transaction(TransactionMode.Manual)]
    public class SingleLineDiagramCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Cancelled;
            Document doc = uidoc.Document;

            HashSet<ElementId> panelIds = PanelScope.Ask(uidoc, "Diagrama unifilar");
            if (panelIds == null) return Result.Cancelled;
            List<FamilyInstance> panels = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(p => CircuitDeviceFilter.IsPanel(p) && (panelIds.Count == 0 || panelIds.Contains(p.Id))).ToList();

            ViewFamilyType draftingType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
            if (draftingType == null || panels.Count == 0) return Result.Cancelled;

            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            DimensioningSettings cfg = DimensioningSettings.Load();
            ViewDrafting last = null;
            int count = 0;

            using (var t = new Transaction(doc, "Diagrama unifilar"))
            {
                t.Start();
                foreach (FamilyInstance panel in panels)
                {
                    List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                        .Where(c => c.BaseEquipment?.Id == panel.Id && c.SystemType == ElectricalSystemType.PowerCircuit)
                        .OrderBy(c => c.StartSlot).ToList();
                    if (circuits.Count == 0) continue;

                    ViewDrafting view = ViewDrafting.Create(doc, draftingType.Id);
                    view.Scale = 50;
                    try { view.Name = $"RE - Unifilar {panel.Name}"; } catch (Exception) { view.Name = $"RE - Unifilar {panel.Name} {panel.Id}"; }

                    double s = RevitHelpers.M(0.6);   // espaçamento entre circuitos
                    double busX = 0, lineEnd = RevitHelpers.M(3.0);
                    double total = circuits.Sum(c => RevitHelpers.ToM(0) + UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes));

                    Line(doc, view, new XYZ(busX, s, 0), new XYZ(busX, -s * circuits.Count, 0));
                    Line(doc, view, new XYZ(busX, s, 0), new XYZ(busX, s * 2.5, 0));
                    Breaker(doc, view, new XYZ(busX, s * 1.6, 0), vertical: true);
                    string panelName = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name;
                    TextNote.Create(doc, view.Id, new XYZ(busX + RevitHelpers.M(0.3), s * 3.2, 0),
                        $"QUADRO {panelName} — carga instalada {total.ToString("N0", br)} VA", textType);
                    if (cfg.DiagramUseDps)
                    {
                        // DPS ligado ao barramento, descendo para a terra
                        double dx = -RevitHelpers.M(1.0), dy = s * 0.5;
                        Line(doc, view, new XYZ(busX, dy, 0), new XYZ(dx, dy, 0));
                        Breaker(doc, view, new XYZ(dx, dy - RevitHelpers.M(0.3), 0), vertical: true);
                        Line(doc, view, new XYZ(dx, dy, 0), new XYZ(dx, dy - RevitHelpers.M(0.15), 0));
                        Line(doc, view, new XYZ(dx, dy - RevitHelpers.M(0.45), 0), new XYZ(dx, dy - RevitHelpers.M(0.7), 0));
                        Line(doc, view, new XYZ(dx - RevitHelpers.M(0.15), dy - RevitHelpers.M(0.7), 0), new XYZ(dx + RevitHelpers.M(0.15), dy - RevitHelpers.M(0.7), 0));
                        Line(doc, view, new XYZ(dx - RevitHelpers.M(0.09), dy - RevitHelpers.M(0.76), 0), new XYZ(dx + RevitHelpers.M(0.09), dy - RevitHelpers.M(0.76), 0));
                        TextNote.Create(doc, view.Id, new XYZ(dx - RevitHelpers.M(2.6), dy - RevitHelpers.M(0.2), 0), cfg.DiagramDpsText, textType);
                    }

                    for (int i = 0; i < circuits.Count; i++)
                    {
                        ElectricalSystem c = circuits[i];
                        double y = -s * i;
                        Line(doc, view, new XYZ(busX, y, 0), new XYZ(lineEnd, y, 0));
                        Breaker(doc, view, new XYZ(RevitHelpers.M(0.8), y, 0), vertical: false);

                        double breaker = SharedParameters.GetNumber(c, SharedParameters.Breaker)
                                         ?? UnitUtils.ConvertFromInternalUnits(c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.AsDouble() ?? 0, UnitTypeId.Amperes);
                        string wiring = SharedParameters.GetText(c, SharedParameters.Wiring) ?? "";
                        double va = UnitUtils.ConvertFromInternalUnits(c.ApparentLoad, UnitTypeId.VoltAmperes);
                        string text = $"C{c.CircuitNumber}  {breaker:0} A  {wiring}  {va.ToString("N0", br)} VA  {c.LoadName}";
                        TextNote.Create(doc, view.Id, new XYZ(lineEnd + RevitHelpers.M(0.15), y + RevitHelpers.M(0.12), 0), text, textType);
                    }
                    last = view;
                    count++;
                }
                t.Commit();
            }

            if (last != null) uidoc.ActiveView = last;
            TaskDialog.Show("Diagrama unifilar", $"{count} diagrama(s) criado(s) em vistas de desenho \"RE - Unifilar ...\".");
            return Result.Succeeded;
        }

        private static void Line(Document doc, View view, XYZ a, XYZ b) =>
            doc.Create.NewDetailCurve(view, Autodesk.Revit.DB.Line.CreateBound(a, b));

        /// <summary>Símbolo simples de disjuntor: um retângulo sobre a linha.</summary>
        private static void Breaker(Document doc, View view, XYZ c, bool vertical)
        {
            double w = RevitHelpers.M(vertical ? 0.15 : 0.3) / 2, h = RevitHelpers.M(vertical ? 0.3 : 0.15) / 2;
            XYZ p1 = c + new XYZ(-w, -h, 0), p2 = c + new XYZ(w, -h, 0), p3 = c + new XYZ(w, h, 0), p4 = c + new XYZ(-w, h, 0);
            Line(doc, view, p1, p2); Line(doc, view, p2, p3); Line(doc, view, p3, p4); Line(doc, view, p4, p1);
        }
    }
}
