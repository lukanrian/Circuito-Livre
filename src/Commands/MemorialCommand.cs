using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RafaelElectrical.Selection;
using RafaelElectrical.Services;
using RafaelElectrical.Views;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Gera o memorial descritivo e de cálculo da instalação elétrica em Word (.docx): capa, sumário,
    /// dados da obra, normas, requisitos gerais, especificação de materiais, memorial de demanda por quadro,
    /// capítulos opcionais e assinaturas. Campos que precisam ser completados ficam destacados em amarelo.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class MemorialCommand : IExternalCommand
    {
        private static readonly string[] AllNorms =
        {
            "ABNT NBR 5410 — Instalações elétricas de baixa tensão",
            "ABNT NBR 5419 — Proteção contra descargas atmosféricas",
            "ABNT NBR 14136 — Plugues e tomadas para uso doméstico e análogo",
            "ABNT NBR IEC 60898 — Disjuntores para instalações domésticas e similares",
            "ABNT NBR NM 247-3 — Cabos isolados com PVC para tensões até 450/750 V",
            "ABNT NBR 15465 — Sistemas de eletrodutos plásticos para instalações elétricas de baixa tensão",
            "ABNT NBR ISO/CIE 8995-1 — Iluminação de ambientes de trabalho",
            "ABNT NBR 14039 — Instalações elétricas de média tensão",
            "ABNT NBR 15575 — Edificações habitacionais — Desempenho",
            "Normas e regulamentos da concessionária local de energia",
        };

        private static readonly (string Name, BuiltInCategory Category, string Spec)[] MaterialCatalog =
        {
            ("Condutores", BuiltInCategory.INVALID,
                "Condutores de cobre eletrolítico, têmpera mole, classe 5, isolação em PVC 70 °C 450/750 V (antichama), ou cabos com isolação EPR/XLPE 90 °C 0,6/1 kV onde indicado em projeto. Seções conforme memorial de cálculo e diagramas."),
            ("Eletrodutos", BuiltInCategory.OST_Conduit,
                "Eletrodutos de PVC rígido ou corrugado (embutidos) conforme NBR 15465, nos diâmetros indicados em projeto, com caixas de passagem a cada no máximo 15 m em trechos retos."),
            ("Eletrocalhas e leitos", BuiltInCategory.OST_CableTray,
                "Eletrocalhas em aço galvanizado, com tampa onde indicado, devidamente aterradas e fixadas com suportes no espaçamento recomendado pelo fabricante."),
            ("Quadros de distribuição", BuiltInCategory.OST_ElectricalEquipment,
                "Quadros de distribuição de embutir ou sobrepor, com barramentos de fase, neutro e terra isolados, espaço de reserva e identificação dos circuitos."),
            ("Disjuntores e dispositivos DR", BuiltInCategory.OST_ElectricalEquipment,
                "Disjuntores termomagnéticos conforme NBR IEC 60898, curva C salvo indicação contrária; dispositivos diferenciais residuais (DR) de alta sensibilidade (30 mA) nos circuitos exigidos pela NBR 5410."),
            ("Tomadas", BuiltInCategory.OST_ElectricalFixtures,
                "Tomadas 2P+T padrão NBR 14136, 10 A ou 20 A conforme indicado, com placa e suporte de linha comercial a definir."),
            ("Interruptores", BuiltInCategory.OST_LightingDevices,
                "Interruptores simples, paralelos ou intermediários, 10 A / 250 V, mesma linha das tomadas."),
            ("Luminárias", BuiltInCategory.OST_LightingFixtures,
                "Luminárias e lâmpadas conforme projeto luminotécnico/arquitetura, preferencialmente LED."),
            ("Telecomunicações", BuiltInCategory.OST_DataDevices,
                "Pontos de telecomunicações com tomadas RJ45 categoria 6 e infraestrutura independente da elétrica."),
        };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            ProjectInfo info = doc.ProjectInformation;
            var prefill = new Dictionary<string, string>
            {
                ["Obra"] = info?.Name ?? "",
                ["Endereço"] = info?.Address ?? "",
                ["Cliente"] = info?.ClientName ?? "",
                ["Construtora"] = info?.OrganizationName ?? "",
            };

            List<Level> levels = RevitHelpers.Levels(doc);
            List<FamilyInstance> panels = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(CircuitDeviceFilter.IsPanel).OrderBy(p => p.Name).ToList();

            HashSet<BuiltInCategory> present = PresentCategories(doc);
            var window = new MemorialWindow(prefill,
                levels.Select(l => new CheckItem($"{l.Name}  ({RevitHelpers.ToM(l.ProjectElevation):N2} m)", l, true)).ToList(),
                panels.Select(p => new CheckItem(p.Name, p, true)).ToList(),
                AllNorms.Select(n => new CheckItem(n, n, !n.Contains("14039") && !n.Contains("15575"))).ToList(),
                MaterialCatalog.Select(m => new CheckItem(m.Name, m,
                    m.Category == BuiltInCategory.INVALID || present.Contains(m.Category))).ToList());
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            var dialog = new SaveFileDialog
            {
                Title = "Salvar memorial",
                Filter = "Documento do Word (*.docx)|*.docx",
                FileName = "Memorial_Descritivo_Eletrico.docx",
                InitialDirectory = string.IsNullOrEmpty(doc.PathName) ? null : Path.GetDirectoryName(doc.PathName),
            };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            Build(doc, window).Save(dialog.FileName);
            TaskDialog.Show("Memorial descritivo", $"Memorial gerado:\n{dialog.FileName}\n\nAo abrir no Word, confirme a atualização dos campos para gerar o sumário. Complete os trechos em amarelo.");
            return Result.Succeeded;
        }

        private static string Field(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out string v) && !string.IsNullOrWhiteSpace(v) ? v : $"[[{key.ToUpperInvariant()}]]";

        private static DocxBuilder Build(Document doc, MemorialWindow w)
        {
            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            Dictionary<string, string> d = w.Data;
            var docx = new DocxBuilder();
            int chapter = 0;
            string H(string title) => $"{++chapter}. {title}";

            // Capa
            docx.Title("MEMORIAL DESCRITIVO E DE CÁLCULO", "PROJETO DE INSTALAÇÕES ELÉTRICAS")
                .Blank(3)
                .Paragraph($"Obra: {Field(d, "Obra")}", true, true)
                .Paragraph($"Endereço: {Field(d, "Endereço")}", false, true)
                .Paragraph($"Construtora: {Field(d, "Construtora")}", false, true)
                .Paragraph($"Cliente: {Field(d, "Cliente")}", false, true)
                .Blank(6)
                .Paragraph($"Responsável técnico: {Field(d, "Responsável técnico")} — CREA {Field(d, "CREA")}", false, true)
                .Paragraph(DateTime.Now.ToString("MMMM 'de' yyyy", br), false, true)
                .PageBreak()
                .TableOfContents()
                .PageBreak();

            // Dados da obra
            docx.Heading1(H("DADOS DA OBRA"))
                .Table(new[] { "Item", "Descrição" }, new List<IList<string>>
                {
                    new[] { "Obra", Field(d, "Obra") },
                    new[] { "Endereço", Field(d, "Endereço") },
                    new[] { "Construtora", Field(d, "Construtora") },
                    new[] { "Tipologia", Field(d, "Tipologia") },
                    new[] { "Características", Field(d, "Características") },
                    new[] { "Nº de apartamentos", Field(d, "Nº de apartamentos") },
                    new[] { "Nº de lojas", Field(d, "Nº de lojas") },
                });

            List<Level> levels = w.Levels.Where(i => i.IsChecked).Select(i => (Level)i.Tag).OrderBy(l => l.ProjectElevation).ToList();
            if (levels.Count > 0)
            {
                docx.Heading2("Níveis da edificação")
                    .Table(new[] { "Nível", "Elevação (m)" },
                        levels.Select(l => (IList<string>)new[] { l.Name, RevitHelpers.ToM(l.ProjectElevation).ToString("N2", br) }));
            }

            // Normas
            List<string> norms = w.Norms.Where(i => i.IsChecked).Select(i => (string)i.Tag).ToList();
            if (norms.Count > 0)
            {
                docx.Heading1(H("NORMAS TÉCNICAS APLICADAS"))
                    .Paragraph("O projeto foi elaborado de acordo com as seguintes normas e regulamentos, em suas versões vigentes:");
                norms.ForEach(n => docx.Bullet(n));
            }

            // Requisitos gerais
            docx.Heading1(H("REQUISITOS GERAIS"))
                .Paragraph("A instalação será executada conforme os desenhos, este memorial e as normas citadas. Divergências entre projeto e obra deverão ser comunicadas ao responsável técnico antes da execução.")
                .Paragraph("Os circuitos foram dimensionados pelos critérios da NBR 5410: capacidade de condução de corrente (com fatores de correção de temperatura e de agrupamento), seção mínima, queda de tensão e coordenação entre condutores e dispositivos de proteção (Ib ≤ In ≤ Iz).")
                .Paragraph($"Sistema de alimentação: [[TENSÃO / SISTEMA DE ATERRAMENTO, ex.: 127/220 V, TN-S]]. Todos os circuitos possuem condutor de proteção (PE) e as massas são ligadas ao barramento de equipotencialização principal.")
                .Paragraph("Os circuitos de tomadas de áreas molhadas, áreas externas e cozinhas são protegidos por dispositivo DR de alta sensibilidade (30 mA), conforme NBR 5410.");

            // Materiais
            var materials = w.Materials.Where(i => i.IsChecked).Select(i => ((string Name, BuiltInCategory Category, string Spec))i.Tag).ToList();
            if (materials.Count > 0)
            {
                docx.Heading1(H("ESPECIFICAÇÃO DOS MATERIAIS"));
                foreach (var m in materials)
                    docx.Heading2(m.Name).Paragraph(m.Spec);
            }

            // Memorial de demanda por quadro
            List<FamilyInstance> panels = w.Panels.Where(i => i.IsChecked).Select(i => (FamilyInstance)i.Tag).ToList();
            if (panels.Count > 0)
            {
                docx.Heading1(H("MEMORIAL DE CÁLCULO DOS QUADROS"));
                foreach (FamilyInstance panel in panels)
                {
                    List<ElectricalSystem> circuits = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                        .Where(c => c.BaseEquipment?.Id == panel.Id && c.SystemType == ElectricalSystemType.PowerCircuit)
                        .OrderBy(c => c.StartSlot).ToList();
                    double installed = circuits.Sum(c => VA(c.ApparentLoad));
                    double demand = SharedParameters.GetNumber(panel, SharedParameters.PanelDemand)
                                    ?? VA(panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTALESTLOAD_PARAM)?.AsDouble() ?? 0);
                    if (demand <= 0) demand = installed;
                    double factor = installed > 0 ? demand / installed : 1;

                    docx.Heading2($"Quadro {panel.Name}")
                        .Table(new[] { "Carga instalada (VA)", "Fator de demanda (%)", "Demanda (VA)", "Nº de circuitos" },
                            new List<IList<string>>
                            {
                                new[] { installed.ToString("N0", br), (factor * 100).ToString("N1", br), demand.ToString("N0", br), circuits.Count.ToString(br) },
                            });

                    if (w.Detailed && circuits.Count > 0)
                    {
                        docx.Table(new[] { "Circ.", "Descrição", "Potência (VA)", "Ib (A)", "Fiação", "Disj. (A)", "ΔV (%)" },
                            circuits.Select(c => (IList<string>)new[]
                            {
                                c.CircuitNumber, c.LoadName ?? "",
                                VA(c.ApparentLoad).ToString("N0", br),
                                (SharedParameters.GetNumber(c, SharedParameters.Current) ?? UnitUtils.ConvertFromInternalUnits(c.ApparentCurrent, UnitTypeId.Amperes)).ToString("N2", br),
                                SharedParameters.GetText(c, SharedParameters.Wiring) ?? "[[DIMENSIONAR]]",
                                SharedParameters.GetNumber(c, SharedParameters.Breaker)?.ToString("N0", br) ?? "",
                                SharedParameters.GetNumber(c, SharedParameters.VoltageDrop)?.ToString("N2", br) ?? "",
                            }));
                    }
                }
            }

            if (w.IncludePerformance)
            {
                docx.Heading1(H("NORMA DE DESEMPENHO (NBR 15575)"))
                    .Paragraph("As instalações elétricas atendem aos requisitos aplicáveis da NBR 15575 quanto à segurança no uso e operação, durabilidade e manutenibilidade, com dispositivos de proteção adequados, identificação dos circuitos e acesso aos quadros para manutenção.")
                    .Paragraph("[[COMPLETAR COM OS REQUISITOS ESPECÍFICOS DO EMPREENDIMENTO]]");
            }

            if (w.IncludeConditions)
            {
                docx.Heading1(H("CONDIÇÕES DE CONSTRUÇÃO, MONTAGEM, OPERAÇÃO E MANUTENÇÃO"))
                    .Bullet("Executar a instalação com profissionais habilitados, com o circuito desenergizado e bloqueado durante intervenções.")
                    .Bullet("Antes da energização, realizar os ensaios de continuidade dos condutores de proteção, resistência de isolamento e funcionamento dos dispositivos DR.")
                    .Bullet("Manter atualizada a identificação dos circuitos nos quadros e os documentos \"como construído\".")
                    .Bullet("Inspecionar periodicamente quadros, conexões e dispositivos de proteção; testar os DR mensalmente pelo botão de teste.")
                    .Bullet("Não alterar a instalação (aumento de cargas, novos circuitos) sem avaliação de profissional habilitado.");
            }

            // Assinaturas
            docx.Heading1(H("ASSINATURAS"))
                .Blank(3)
                .Paragraph("_______________________________________", false, true)
                .Paragraph($"{Field(d, "Responsável técnico")} — Responsável técnico — CREA {Field(d, "CREA")}", false, true)
                .Blank(3)
                .Paragraph("_______________________________________", false, true)
                .Paragraph($"{Field(d, "Cliente")} — Cliente — CNPJ {Field(d, "CNPJ do cliente")}", false, true);

            return docx;
        }

        private static double VA(double internalPower)
        {
            try { return UnitUtils.ConvertFromInternalUnits(internalPower, UnitTypeId.VoltAmperes); }
            catch (Exception) { return 0; }
        }

        private static HashSet<BuiltInCategory> PresentCategories(Document doc)
        {
            var set = new HashSet<BuiltInCategory>();
            var docs = new List<Document> { doc };
            docs.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l => l.GetLinkDocument()).Where(x => x != null));
            foreach (var m in MaterialCatalog)
            {
                if (m.Category == BuiltInCategory.INVALID) continue;
                if (docs.Any(x => new FilteredElementCollector(x).OfCategory(m.Category).WhereElementIsNotElementType().GetElementCount() > 0))
                    set.Add(m.Category);
            }
            return set;
        }
    }
}
