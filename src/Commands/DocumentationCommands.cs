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

        /// <summary>Opção já escolhida pelo botão (null pergunta).</summary>
        protected virtual TaskDialogResult? Preset => null;

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
            switch (Preset ?? dialog.Show())
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
            DimensioningSettings cfgTags = DimensioningSettings.Load();

            using (var t = new Transaction(doc, "Criar identificadores"))
            {
                t.Start();
                foreach (Element e in targets.Where(e => e != null && !alreadyTagged.Contains(e.Id) && e.Category != null))
                {
                    XYZ p = RevitHelpers.PointOf(e);
                    if (p == null) continue;
                    try
                    {
                        ElementId tagType = TagCatalog.Configured(doc, e, cfgTags);
                        if (tagType != null)
                            IndependentTag.Create(doc, tagType, view.Id, new Reference(e), false, TagOrientation.Horizontal, p + new XYZ(off, off, 0));
                        else
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

            var dialog = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", FileName = "Relatorio_Fiacao.xlsx", Title = "Salvar relatório de fiação" };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                string ColorX(string role) => colors.TryGetValue(role, out string v) ? v : "";
                var byPanel = new List<IList<object>> { new object[] { "Quadro", "Função", "Cabo", "Cor", "Comprimento (m)" } };
                byPanel.AddRange(totals.Select(kv => (IList<object>)new object[] { kv.Key.Panel, kv.Key.Role, kv.Key.Cable, ColorX(kv.Key.Role), Math.Round(kv.Value, 1) }));
                var overall = new List<IList<object>> { new object[] { "Função", "Cabo", "Cor", "Comprimento (m)" } };
                overall.AddRange(totals.GroupBy(kv => (kv.Key.Role, kv.Key.Cable)).OrderBy(g => g.Key.Role).ThenBy(g => g.Key.Cable)
                    .Select(g => (IList<object>)new object[] { g.Key.Role, g.Key.Cable, ColorX(g.Key.Role), Math.Round(g.Sum(x => x.Value), 1) }));
                new XlsxWriter().Sheet("Por quadro", byPanel).Sheet("Total geral", overall).Save(dialog.FileName);
                TaskDialog.Show("Relatório de fiação", $"Relatório exportado (folga {extra:0.#} %)." +
                    (noLength > 0 ? $"\n{noLength} circuito(s) sem comprimento ficaram de fora (rode o Atualizar Quadros)." : ""));
                return Result.Succeeded;
            }

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

}
