using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using CircuitoLivre.Services;

namespace CircuitoLivre.Views
{
    /// <summary>
    /// Janela de configurações do Circuito Livre (montada em código): geral, fiação, agrupamento, ocupação,
    /// dimensionamento, telecom, documentação e exportação/importação em JSON.
    /// </summary>
    public class SettingsWindow : Window
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public DimensioningSettings Settings { get; private set; }

        private readonly List<Action<DimensioningSettings>> _load = new();
        private readonly List<Func<DimensioningSettings, string>> _save = new();

        /// <summary>Marcado: gravar as configurações dentro do projeto ativo.</summary>
        public bool SaveInProject => _projectCheck?.IsChecked == true;

        private readonly bool? _projectOption;
        private CheckBox _projectCheck;

        /// <param name="projectOption">null = sem projeto ativo; true/false = o projeto já tem configurações próprias.</param>
        private readonly SettingsContext _ctx;

        public SettingsWindow(DimensioningSettings settings, bool? projectOption = null, SettingsContext context = null)
        {
            _projectOption = projectOption;
            _ctx = context ?? new SettingsContext();
            Title = $"Circuito Livre {RevitHelpers.PluginVersion} — Configurações" +
                    (projectOption == true ? " (deste projeto)" : "");
            Width = 990;
            Height = 650;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Settings = settings.Clone();

            var tabs = new TabControl { TabStripPlacement = Dock.Left, Margin = new Thickness(8) };
            tabs.Items.Add(Tab("Geral", GeneralTab()));
            tabs.Items.Add(Tab("Fiação", WiringTab()));
            tabs.Items.Add(Tab("Agrupamento", GroupingTab()));
            tabs.Items.Add(Tab("Ocupação", OccupancyTab()));
            tabs.Items.Add(Tab("Conduítes", ConduitsTab()));
            tabs.Items.Add(Tab("Bandejas", TraysTab()));
            tabs.Items.Add(Tab("Telecom", TelecomTab()));
            tabs.Items.Add(Tab("Identificadores", TagsTab()));
            tabs.Items.Add(Tab("Cota do conduíte", HeightsTab()));
            tabs.Items.Add(Tab("Dimensionamento", SizingTab()));
            tabs.Items.Add(Tab("Concessionária", UtilityTab()));
            tabs.Items.Add(Tab("Documentação", DocumentationTab()));
            tabs.Items.Add(Tab("Projeto e arquivos", ExchangeTab()));

            var cancel = new Button { Content = "Cancelar", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
            ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
            var defaults = new Button { Content = "Restaurar padrões", Width = 130 };
            defaults.Click += (_, _) => Fill(new DimensioningSettings());

            var buttons = new DockPanel { Margin = new Thickness(8, 0, 8, 8), LastChildFill = false };
            DockPanel.SetDock(defaults, Dock.Left);
            DockPanel.SetDock(ok, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            buttons.Children.Add(defaults);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(tabs);
            Content = root;

            Fill(Settings);
        }

        // ------------------------------------------------------------------ abas

        private UIElement GeneralTab()
        {
            var p = NewPanel();
            Group(p, "Propriedades do projeto");
            Choice(p, "Parâmetro da seção dos cabos dos circuitos:",
                new[] { "CL_Seção (mm²)", "F.C.A", "F.C.T", "I Considerado", "Seção do Condutor Adotado (mm²)", "Queda de Tensão" },
                s => s.CircuitSectionParameter, (s, v) => s.CircuitSectionParameter = v);
            Note(p, "O dimensionamento grava a seção em CL_Seção (mm²); os demais nomes são mantidos para compatibilidade com projetos existentes.");
            Number(p, "Temperatura ambiente (°C):", s => s.TemperatureC, (s, v) => s.TemperatureC = v, 0, 80);
            SectionCombo(p, "Seção a omitir nos identificadores de fiação:", s => s.OmitSectionInTags, (s, v) => s.OmitSectionInTags = v, allowNone: true);
            SectionCombo(p, "Seção máxima de dimensionamento (mm²):", s => s.MaxSection, (s, v) => s.MaxSection = v, allowNone: false);
            Group(p, "Idioma do plugin (reinicie o Revit após mudar)");
            Choice(p, "Idioma:", new[] { "Mesmo do Revit", "Português Brasileiro", "English", "Español" },
                s => s.InterfaceLanguage, (s, v) => s.InterfaceLanguage = v);
            Note(p, "A tradução da interface ainda não está disponível; os comandos permanecem em português.");
            Group(p, "Seções mínimas (Tabela 47)");
            SectionCombo(p, "Circuitos de iluminação (mm²):", s => s.MinSectionLighting, (s, v) => s.MinSectionLighting = v, allowNone: false);
            SectionCombo(p, "Circuitos de força (mm²):", s => s.MinSectionPower, (s, v) => s.MinSectionPower = v, allowNone: false);
            Group(p, "Identificação de retornos");
            Radio(p, "returns", "Separar retornos de luminárias e interruptores nos identificadores", s => !s.JoinReturns, s => s.JoinReturns = false);
            Radio(p, "returns", "Unir retornos de luminárias e interruptores nos identificadores", s => s.JoinReturns, s => s.JoinReturns = true);
            Group(p, "Ordenação das legendas de fiação");
            Radio(p, "legend", "Numérica (1, 2, 3...)", s => !s.LegendAlphabetic, s => s.LegendAlphabetic = false);
            Radio(p, "legend", "Alfabética (A, B, C...)", s => s.LegendAlphabetic, s => s.LegendAlphabetic = true);
            Group(p, "Recursos necessários");
            Note(p, "Os parâmetros CL_ são criados automaticamente no projeto na primeira vez que cada comando precisa deles.");
            p.Children.Add(new CheckBox { Content = "Carregar famílias necessárias", IsEnabled = false,
                ToolTip = "Nenhuma família RFA é distribuída com este plugin." });
            p.Children.Add(new CheckBox { Content = "Carregar parâmetros dos cabos elétricos (opcional)", IsEnabled = false,
                ToolTip = "Os parâmetros CL_ são criados automaticamente pelos comandos elétricos." });
            p.Children.Add(new CheckBox { Content = "Carregar parâmetros dos cabos de telecom (opcional)", IsEnabled = false,
                ToolTip = "Pacote de parâmetros de telecom ainda não disponível." });
            return Scroll(p);
        }

        private UIElement WiringTab()
        {
            var tabs = new TabControl();
            tabs.Items.Add(Tab("Geral", WiringGeneralTab()));
            tabs.Items.Add(Tab("Seções", WiringSectionsTab()));
            tabs.Items.Add(Tab("Diâmetros", WiringDiametersTab()));
            return tabs;
        }

        private UIElement WiringGeneralTab()
        {
            var p = NewPanel();
            Group(p, "Cores dos cabos");
            Text(p, "Fase A:", s => s.ColorPhaseA, (s, v) => s.ColorPhaseA = v);
            Text(p, "Fase B:", s => s.ColorPhaseB, (s, v) => s.ColorPhaseB = v);
            Text(p, "Fase C:", s => s.ColorPhaseC, (s, v) => s.ColorPhaseC = v);
            Text(p, "Neutro:", s => s.ColorNeutral, (s, v) => s.ColorNeutral = v);
            Text(p, "Terra:", s => s.ColorGround, (s, v) => s.ColorGround = v);
            Text(p, "Retorno:", s => s.ColorReturn, (s, v) => s.ColorReturn = v);
            Group(p, "Quantitativo");
            Number(p, "Fator de correção do comprimento (ex.: 1,20 = +20%):", s => s.WiringFactor, (s, v) => s.WiringFactor = v, 1, 3);
            Group(p, "Quantitativo de fiação");
            Radio(p, "wire-report", "Usar descrição dos cabos por extenso", s => s.WiringReportExpandedDescription,
                s => s.WiringReportExpandedDescription = true);
            Radio(p, "wire-report", "Usar descrição resumida e propriedades em colunas separadas", s => !s.WiringReportExpandedDescription,
                s => s.WiringReportExpandedDescription = false);
            Group(p, "Roteamento de sensores de presença bipolares");
            Radio(p, "sensor-return", "Luminárias recebem dois cabos de retorno", s => s.BipolarSensorTwoReturns,
                s => s.BipolarSensorTwoReturns = true);
            Radio(p, "sensor-return", "Luminárias recebem a primeira fase e um retorno", s => !s.BipolarSensorTwoReturns,
                s => s.BipolarSensorTwoReturns = false);
            Note(p, "O roteamento automático de sensores bipolares ainda não está implementado.");
            Group(p, "Espaçamento das tags de fiação");
            Check(p, "Adicionar espaçamento extra em tags de circuitos sem terra", s => s.SpaceTagsWithoutGround,
                (s, v) => s.SpaceTagsWithoutGround = v);
            Group(p, "Seções do neutro e do terra");
            Check(p, "Neutro reduzido nos circuitos trifásicos com fase > 25 mm² (Tabela 48 — só se a 3ª harmônica e o desequilíbrio permitirem)",
                s => s.ReducedNeutral, (s, v) => s.ReducedNeutral = v);
            Note(p, "O condutor de proteção segue a Tabela 58 (até 16 mm² igual à fase; 25–35 mm² → 16 mm²; acima, metade da fase).");
            return Scroll(p);
        }

        private UIElement WiringSectionsTab()
        {
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                Margin = new Thickness(8), HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridTextColumn { Header = "Fase (mm²)", Binding = new Binding(nameof(CableSectionRule.Phase)), IsReadOnly = true });
            double[] sections = Nbr5410.Sections.Where(x => x >= 1.5).Concat(new double[] { 400, 500, 630, 800, 1000 }).Distinct().OrderBy(x => x).ToArray();
            foreach (string name in new[] { nameof(CableSectionRule.Neutral), nameof(CableSectionRule.Ground), nameof(CableSectionRule.Return) })
                grid.Columns.Add(new DataGridComboBoxColumn { Header = name == nameof(CableSectionRule.Ground) ? "Terra" : name == nameof(CableSectionRule.Return) ? "Retorno" : "Neutro",
                    ItemsSource = sections, SelectedItemBinding = new Binding(name) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            _load.Add(s => grid.ItemsSource = s.CableSections);
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (CableSectionRule row in s.CableSections)
                    if (row.Neutral <= 0 || row.Ground <= 0 || row.Return <= 0 ||
                        row.Neutral > row.Phase || row.Ground > row.Phase || row.Return > row.Phase)
                        return $"Seções inválidas para fase {row.Phase} mm².";
                return null;
            });
            return grid;
        }

        private UIElement WiringDiametersTab()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            var choices = new StackPanel { Orientation = Orientation.Horizontal };
            var type = new ComboBox { Width = 180, Margin = new Thickness(4), ItemsSource = new[] { "Isolado", "Unipolar", "Multipolar" } };
            var insulation = new ComboBox { Width = 140, Margin = new Thickness(4), ItemsSource = new[] { "PVC", "EPR/XLPE" } };
            choices.Children.Add(new TextBlock { Text = "Tipo de cabo", VerticalAlignment = VerticalAlignment.Center });
            choices.Children.Add(type);
            choices.Children.Add(new TextBlock { Text = "Isolação", VerticalAlignment = VerticalAlignment.Center });
            choices.Children.Add(insulation);
            DockPanel.SetDock(choices, Dock.Top);
            root.Children.Add(choices);
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
            grid.Columns.Add(new DataGridTextColumn { Header = "Seção (mm²)", Binding = new Binding(nameof(CableDiameterRule.Section)), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Diâmetro externo (mm)",
                Binding = new Binding(nameof(CableDiameterRule.DiameterMm)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus } });
            root.Children.Add(grid);
            DimensioningSettings active = null;
            void ShowProfile()
            {
                if (active == null || type.SelectedItem == null || insulation.SelectedItem == null) return;
                string key = type.SelectedItem + "|" + insulation.SelectedItem;
                if (!active.CableDiameters.TryGetValue(key, out var rows))
                    active.CableDiameters[key] = rows = Nbr5410.Sections.Where(x => x >= 1.5)
                        .Select(x => new CableDiameterRule { Section = x, DiameterMm = Math.Round(active.CableDiameter(x), 2) }).ToList();
                grid.ItemsSource = rows;
            }
            type.SelectionChanged += (_, _) => ShowProfile();
            insulation.SelectionChanged += (_, _) => ShowProfile();
            _load.Add(s => { active = s; type.SelectedIndex = 0; insulation.SelectedIndex = 0; ShowProfile(); });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                return s.CableDiameters.Values.SelectMany(x => x).Any(x => !double.IsFinite(x.DiameterMm) || x.DiameterMm <= 0 || x.DiameterMm > 100)
                    ? "Diâmetro externo de cabo inválido." : null;
            });
            return root;
        }

        private UIElement GroupingTab()
        {
            var p = NewPanel();
            Note(p, "Fatores de agrupamento (NBR 5410, Tabela 42). O roteamento usa a tabela do tipo de cada trecho: eletroduto = fechada; " +
                    "eletrocalha pelo nome do tipo (\"perfurada\", \"leito\"/\"escada\", senão lisa). Confira os valores com a sua edição da norma.");
            GroupingRow(p, "Infraestrutura fechada (eletroduto / feixe)",
                new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9–11", "12–15", "16–19", "≥20" },
                s => s.GroupingClosed, (s, v) => s.GroupingClosed = v);
            string[] nine = { "1", "2", "3", "4", "5", "6", "7", "8", "≥9" };
            GroupingRow(p, "Eletrocalhas lisas (camada única)", nine, s => s.GroupingSmoothTray, (s, v) => s.GroupingSmoothTray = v);
            GroupingRow(p, "Eletrocalhas perfuradas (camada única)", nine, s => s.GroupingPerforatedTray, (s, v) => s.GroupingPerforatedTray = v);
            GroupingRow(p, "Leito / escada para cabos", nine, s => s.GroupingLadder, (s, v) => s.GroupingLadder = v);
            Group(p, "Sem roteamento");
            Integer(p, "Circuitos agrupados considerados quando o circuito não foi roteado:", s => s.GroupedCircuits, (s, v) => s.GroupedCircuits = v, 1, 50);
            return Scroll(p);
        }

        private UIElement OccupancyTab()
        {
            var p = NewPanel();
            Group(p, "Taxa máxima de ocupação dos eletrodutos");
            Number(p, "Um cabo (%):", s => s.OccupancyOneCable, (s, v) => s.OccupancyOneCable = v, 1, 100);
            Number(p, "Dois cabos (%):", s => s.OccupancyTwoCables, (s, v) => s.OccupancyTwoCables = v, 1, 100);
            Number(p, "Três ou mais cabos (%):", s => s.OccupancyMoreCables, (s, v) => s.OccupancyMoreCables = v, 1, 100);
            return Scroll(p);
        }

        private UIElement SizingTab()
        {
            var p = NewPanel();
            Group(p, "Método de dimensionamento dos disjuntores");
            Radio(p, "breaker", "Pela corrente de projeto: In ≥ Ib e cabo com Iz ≥ In",
                s => s.BreakerMethod == BreakerMethod.DesignCurrent, s => s.BreakerMethod = BreakerMethod.DesignCurrent);
            Radio(p, "breaker", "Pela capacidade de condução corrigida: cabo com Iz ≥ Ib e maior disjuntor com Ib ≤ In ≤ Iz",
                s => s.BreakerMethod == BreakerMethod.Capacity, s => s.BreakerMethod = BreakerMethod.Capacity);
            Group(p, "Queda de tensão");
            Number(p, "Queda máxima no circuito terminal (%):", s => s.MaxVoltageDropPercent, (s, v) => s.MaxVoltageDropPercent = v, 0.1, 10);
            Check(p, "Verificar a queda total (alimentadores + circuito)", s => s.CheckTotalVoltageDrop, (s, v) => s.CheckTotalVoltageDrop = v);
            Radio(p, "total", "A partir do ponto de entrega (ΔV total = 5%)",
                s => s.TotalVoltageDropPercent < 6, s => s.TotalVoltageDropPercent = 5);
            Radio(p, "total", "A partir dos terminais do transformador ou gerador próprio (ΔV total = 7%)",
                s => s.TotalVoltageDropPercent >= 6, s => s.TotalVoltageDropPercent = 7);
            Number(p, "Resistividade do cobre (Ω·mm²/m):", s => s.Resistivity, (s, v) => s.Resistivity = v, 0.001, 1);
            Group(p, "Proteção DR");
            Check(p, "Adicionar disjuntor antes dos grupos de DR quando a corrente nominal do DR for menor que a do disjuntor geral",
                s => s.BreakerBeforeDr, (s, v) => s.BreakerBeforeDr = v);
            Integer(p, "Máximo de circuitos por DR:", s => s.DrMaxCircuits, (s, v) => s.DrMaxCircuits = v, 1, 30);
            Number(p, "Sensibilidade do DR (mA):", s => s.DrSensitivityMa, (s, v) => s.DrSensitivityMa = v, 10, 500);
            Text(p, "Descrições que exigem DR (separadas por ;):", s => s.DrKeywords, (s, v) => s.DrKeywords = v);
            Group(p, "Temperatura de sobrecarga dos cabos");
            Radio(p, "overload", "Sobrecarga ABAIXO do tempo normativo", s => !s.OverloadAboveNormativeTime, s => s.OverloadAboveNormativeTime = false);
            Radio(p, "overload", "Sobrecarga ACIMA do tempo normativo", s => s.OverloadAboveNormativeTime, s => s.OverloadAboveNormativeTime = true);
            Group(p, "Critério de múltiplos condutores");
            Radio(p, "ncf", "Quantidade de condutores por neutro e por terra é sempre igual a um", s => !s.MultipleConductorsNcf, s => s.MultipleConductorsNcf = false);
            Radio(p, "ncf", "Por neutro = N.C.F. e por terra = metade do N.C.F.", s => s.MultipleConductorsNcf, s => s.MultipleConductorsNcf = true);
            Group(p, "Classes de circuito (pela descrição do circuito)");
            ClassesTable(p);
            Group(p, "Regras condicionais de dimensionamento");
            SizingRulesTable(p);
            Group(p, "Revit");
            Check(p, "Atualizar o disjuntor nativo (Classificação) do circuito", s => s.UpdateRevitRating, (s, v) => s.UpdateRevitRating = v);
            return Scroll(p);
        }

        private UIElement TelecomTab()
        {
            var tabs = new TabControl();
            tabs.Items.Add(Tab("Geral", TelecomGeneralTab()));
            tabs.Items.Add(Tab("Fiação", TelecomWiringTab()));
            tabs.Items.Add(Tab("Cabo", TelecomNamesTab()));
            tabs.Items.Add(Tab("Diâmetros", TelecomDiametersTab()));
            return tabs;
        }

        private UIElement TelecomGeneralTab()
        {
            var p = NewPanel();
            Group(p, "Painel de dados");
            Text(p, "Modelo de tabela:", s => s.TelecomPanelSchedule, (s, v) => s.TelecomPanelSchedule = v);
            Check(p, "Adicionar dimensão aos identificadores", s => s.TelecomTagDimension,
                (s, v) => s.TelecomTagDimension = v);
            Note(p, "O modelo de tabela e a dimensão da tag são guardados para uso quando houver famílias compatíveis no projeto.");
            return Scroll(p);
        }

        private UIElement TelecomWiringTab()
        {
            var p = NewPanel();
            Group(p, "Nomenclatura de cabos");
            Radio(p, "telecom-name", "Mostrar apenas quantidade e tipo de cabo", s => !s.TelecomVerboseNomenclature,
                s => s.TelecomVerboseNomenclature = false);
            Radio(p, "telecom-name", "Mostrar quantidade, tipo de cabo, quadro alimentador e circuitos de destino",
                s => s.TelecomVerboseNomenclature, s => s.TelecomVerboseNomenclature = true);
            Group(p, "Cabo utilizado por sistema (relatório de fiação)");
            string[] names = { "Dados", "Telefone", "Comunicações", "Segurança" };
            Func<DimensioningSettings, string>[] get = { s => s.CableData, s => s.CableTelephone, s => s.CableCommunication, s => s.CableSecurity };
            Action<DimensioningSettings, string>[] set = { (s, v) => s.CableData = v, (s, v) => s.CableTelephone = v,
                (s, v) => s.CableCommunication = v, (s, v) => s.CableSecurity = v };
            for (int i = 0; i < names.Length; i++)
            {
                Choice(p, names[i] + ":", TelecomCableCatalog, get[i], set[i]);
            }
            Group(p, "Ocupação");
            Number(p, "Diâmetro externo médio do cabo de telecom (mm):", s => s.TelecomCableDiameterMm, (s, v) => s.TelecomCableDiameterMm = v, 1, 50);
            return Scroll(p);
        }

        private static readonly string[] TelecomCableCatalog =
        {
            "UTP_CAT-5e", "UTP_CAT-6", "FTP_CAT-6", "FTP_CAT-6a", "FTP_CAT-7",
            "CCI_1P", "CCI_2P", "CCI_3P", "CCI_4P", "CI_10P", "CI_20P", "CI_30P", "CI_40P",
            "RG59_47%", "RG59_67%", "RG59_95%", "RG6_67%", "RG6_90%", "RG6_95%",
            "FO_N2", "FO_N4", "FO_N6", "FO_N8", "FO_N12"
        };

        private UIElement TelecomNamesTab() => TelecomTable(false);
        private UIElement TelecomDiametersTab() => TelecomTable(true);

        private UIElement TelecomTable(bool diameters)
        {
            var rows = new List<TelecomCableRow>();
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                Margin = new Thickness(8) };
            grid.Columns.Add(new DataGridTextColumn { Header = "Cabo", Binding = new Binding(nameof(TelecomCableRow.Cable)), IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = diameters ? "Diâmetro externo (mm)" : "Nome personalizado",
                Binding = new Binding(diameters ? nameof(TelecomCableRow.Diameter) : nameof(TelecomCableRow.Name)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _load.Add(s =>
            {
                rows = TelecomCableCatalog.Union(s.TelecomCableDiameters.Keys).Union(s.TelecomCableNames.Keys)
                    .Select(c => new TelecomCableRow { Cable = c, Name = s.TelecomName(c),
                        Diameter = s.TelecomDiameter(c).ToString("0.##", Br) }).ToList();
                grid.ItemsSource = rows;
            });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (var row in rows)
                {
                    if (diameters)
                    {
                        if (!TryNumber(row.Diameter, out double value) || value <= 0 || value > 100)
                            return $"Diâmetro inválido para {row.Cable}.";
                        s.TelecomCableDiameters[row.Cable] = value;
                    }
                    else s.TelecomCableNames[row.Cable] = string.IsNullOrWhiteSpace(row.Name) ? row.Cable : row.Name.Trim();
                }
                return null;
            });
            return grid;
        }

        private sealed class TelecomCableRow
        {
            public string Cable { get; set; }
            public string Name { get; set; }
            public string Diameter { get; set; }
        }

        private UIElement DocumentationTab()
        {
            var tabs = new TabControl { Margin = new Thickness(4) };
            tabs.Items.Add(Tab("Identificadores", DocumentationTagsTab()));
            tabs.Items.Add(Tab("Diagramas", DocumentationDiagramsTab()));
            tabs.Items.Add(Tab("Detalhe da proteção", DocumentationProtectionTab()));
            tabs.Items.Add(Tab("Mapear vistas", DocumentationViewsTab()));
            tabs.Items.Add(Tab("Circuitos", DocumentationCircuitsTab()));
            return tabs;
        }

        private UIElement DocumentationTagsTab()
        {
            var p = NewPanel();
            Group(p, "Identificadores de fiação");
            Check(p, "Seção do circuito nos identificadores", s => s.TagShowSection, (s, v) => s.TagShowSection = v);
            Check(p, "Desenhar símbolos dos condutores junto ao eletroduto", s => s.TagWireSymbols, (s, v) => s.TagWireSymbols = v);
            Note(p, "A simbologia usa uma família compatível do projeto/template. O tamanho e o desenho pertencem à família; o tamanho de texto abaixo vale para identificadores textuais.");
            Radio(p, "tagNumbers", "Usar números de cada quadro", s => !s.TagUniqueCircuitNumbers, s => s.TagUniqueCircuitNumbers = false);
            Radio(p, "tagNumbers", "Usar sequência única na planta", s => s.TagUniqueCircuitNumbers, s => s.TagUniqueCircuitNumbers = true);
            Note(p, "Essa sequência vale apenas nos identificadores de fiação; não altera os circuitos e tabelas do Revit.");
            p.Children.Add(new CheckBox { Content = "Identificar luminárias por número", IsEnabled = false });
            Radio(p, "orient", "Identificadores na horizontal", s => !s.TagAlongConduit, s => s.TagAlongConduit = false);
            Radio(p, "orient", "Identificadores no eixo do eletroduto", s => s.TagAlongConduit, s => s.TagAlongConduit = true);
            Number(p, "Tamanho do texto (mm no papel):", s => s.TagTextMm, (s, v) => s.TagTextMm = v, 1, 6);
            p.Children.Add(new CheckBox { Content = "Quantificar fases em parâmetro único", IsEnabled = false });
            Group(p, "Agrupamento de terra");
            Radio(p, "ground", "Não agrupar (um terra por circuito)", s => s.GroundGrouping == GroundGrouping.None, s => s.GroundGrouping = GroundGrouping.None);
            Radio(p, "ground", "Um cabo único de aterramento por trecho", s => s.GroundGrouping == GroundGrouping.OnePerRun, s => s.GroundGrouping = GroundGrouping.OnePerRun);
            Radio(p, "ground", "Agrupar só os circuitos de iluminação", s => s.GroundGrouping == GroundGrouping.LightingOnly, s => s.GroundGrouping = GroundGrouping.LightingOnly);
            Group(p, "Roteamento");
            p.Children.Add(new CheckBox { Content = "Permitir emendas no quadro de distribuição", IsEnabled = false });
            Note(p, "Essas três opções dependem de recursos de roteamento e identificação ainda indisponíveis no plugin.");
            return Scroll(p);
        }

        private UIElement DocumentationDiagramsTab()
        {
            var p = NewPanel();
            Group(p, "Diagramas a criar");
            Check(p, "Criar diagrama unifilar", s => s.DiagramUnifilar, (s, v) => s.DiagramUnifilar = v);
            Check(p, "Criar também o diagrama multifilar", s => s.DiagramMultifilar, (s, v) => s.DiagramMultifilar = v);
            Group(p, "Opções do diagrama");
            Check(p, "Adicionar circuitos reserva ao diagrama", s => s.DiagramShowSpares, (s, v) => s.DiagramShowSpares = v);
            p.Children.Add(new CheckBox { Content = "Adicionar espaços vazios ao diagrama", IsEnabled = false });
            Choice(p, "Tamanho do diagrama:", new[] { "Pequeno", "Médio", "Grande" }, s => s.DiagramSize, (s, v) => s.DiagramSize = v);
            Group(p, "DPS");
            Check(p, "Desenhar DPS no barramento do quadro", s => s.DiagramUseDps, (s, v) => s.DiagramUseDps = v);
            Check(p, "DPS também no neutro", s => s.DiagramDpsOnNeutral, (s, v) => s.DiagramDpsOnNeutral = v);
            Choice(p, "Tensão máxima do DPS (V):", new[] { "75", "175", "275", "385", "460" },
                s => s.DiagramDpsVoltageV.ToString(), (s, v) => s.DiagramDpsVoltageV = int.Parse(v));
            Choice(p, "Corrente máxima do DPS (kA):", new[] { "20", "30", "45" },
                s => s.DiagramDpsCurrentKa.ToString(), (s, v) => s.DiagramDpsCurrentKa = int.Parse(v));
            Check(p, "Adicionar disjuntor ao DPS", s => s.DiagramDpsBreaker, (s, v) => s.DiagramDpsBreaker = v);
            Choice(p, "Disjuntor do DPS (A):", Nbr5410.BreakerRatings.Select(x => x.ToString()),
                s => s.DiagramDpsBreakerA.ToString(), (s, v) => s.DiagramDpsBreakerA = int.Parse(v));
            return Scroll(p);
        }

        private UIElement DocumentationProtectionTab()
        {
            var p = NewPanel();
            Check(p, "Criar detalhe da proteção", s => s.DiagramProtectionDetail, (s, v) => s.DiagramProtectionDetail = v);
            Check(p, "Adicionar resumo de circuitos", s => s.ProtectionShowSummary, (s, v) => s.ProtectionShowSummary = v);
            Check(p, "Adicionar barramento de neutro", s => s.ProtectionShowNeutralBus, (s, v) => s.ProtectionShowNeutralBus = v);
            Check(p, "Adicionar barramento de terra", s => s.ProtectionShowGroundBus, (s, v) => s.ProtectionShowGroundBus = v);
            Choice(p, "Tipo de barramento:", new[] { "DIN", "Espinha de peixe" }, s => s.ProtectionBusType, (s, v) => s.ProtectionBusType = v);
            Integer(p, "Disjuntores por linha (DIN):", s => s.ProtectionBreakersPerRow, (s, v) => s.ProtectionBreakersPerRow = v, 4, 24);
            Integer(p, "Mínimo de circuitos por seção (espinha):", s => s.ProtectionFishboneMinCircuits,
                (s, v) => s.ProtectionFishboneMinCircuits = v, 4, 100);
            return Scroll(p);
        }

        private UIElement DocumentationViewsTab()
        {
            var p = NewPanel();
            Note(p, "Vistas de planta para os identificadores de cada nível. Deixe em branco para usar a vista ativa.");
            foreach (var level in _ctx.Levels)
                Choice(p, level.Name, new[] { "" }.Concat(_ctx.PlanViewsByLevel.TryGetValue(level.Name, out var views)
                    ? views : new List<string>()), s => s.TagViewByLevel.TryGetValue(level.Name, out string view) ? view : "",
                    (s, v) => { if (string.IsNullOrEmpty(v)) s.TagViewByLevel.Remove(level.Name); else s.TagViewByLevel[level.Name] = v; });
            return Scroll(p);
        }

        private UIElement DocumentationCircuitsTab()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            var note = new TextBlock { Text = "Configuração por descrição da carga e número de polos dos circuitos do projeto.",
                Margin = new Thickness(0, 0, 0, 5) };
            DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var rows = new ObservableCollection<CircuitDocumentationRule>();
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
            grid.Columns.Add(new DataGridTextColumn { Header = "Descrição da carga", Binding = new Binding(nameof(CircuitDocumentationRule.LoadName)), IsReadOnly = true,
                Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Polos", Binding = new Binding(nameof(CircuitDocumentationRule.Poles)), IsReadOnly = true });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Tipo de cabo", ItemsSource = new[] { "", "Isolado", "Unipolar", "Multipolar" },
                SelectedItemBinding = new Binding(nameof(CircuitDocumentationRule.CableType)) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Isolação", ItemsSource = new[] { "PVC", "XLPE" },
                SelectedItemBinding = new Binding(nameof(CircuitDocumentationRule.Insulation)) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Neutro", Binding = new Binding(nameof(CircuitDocumentationRule.Neutral)) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Terra", Binding = new Binding(nameof(CircuitDocumentationRule.Ground)) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Curva", ItemsSource = new[] { "B", "C", "D" },
                SelectedItemBinding = new Binding(nameof(CircuitDocumentationRule.Curve)) });
            root.Children.Add(grid);
            _load.Add(s =>
            {
                rows.Clear();
                foreach (string name in _ctx.CircuitLoadNames.Union(s.CircuitDocumentation.Select(r => r.LoadName)).OrderBy(n => n))
                    for (int poles = 1; poles <= 3; poles++)
                    {
                        var saved = s.DocumentationFor(name, poles);
                        var baseRule = s.ClassOf(name);
                        rows.Add(saved == null ? new CircuitDocumentationRule { LoadName = name, Poles = poles,
                            Insulation = s.Insulation == Insulation.EPR90 ? "XLPE" : "PVC",
                            Neutral = baseRule?.Neutral ?? true, Ground = baseRule?.Ground ?? true,
                            Curve = baseRule?.Curve ?? "C" } : saved);
                    }
            });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                s.CircuitDocumentation = rows.ToList();
                return null;
            });
            return root;
        }

        private UIElement ConduitsTab()
        {
            var p = NewPanel();
            Note(p, "Diâmetro padrão de cada tipo de eletroduto. Trechos com outro diâmetro recebem o identificador de dimensão.");
            Number(p, "Diâmetro padrão geral (mm):", s => s.ConduitDefaultDiameterMm, (s, v) => s.ConduitDefaultDiameterMm = v, 5, 300);
            foreach (string type in _ctx.ConduitTypes)
                DictNumber(p, type, s => s.ConduitDefaultDiameter, s => s.ConduitDefaultDiameterMm, 5, 300);
            if (_ctx.ConduitTypes.Count == 0) Note(p, "(abra as Configurações com um projeto aberto para listar os tipos de eletroduto)");
            return Scroll(p);
        }

        private UIElement TraysTab()
        {
            var p = NewPanel();
            Group(p, "Tamanho padrão (não recebe identificador de dimensão)");
            Number(p, "Largura (mm):", s => s.TrayDefaultWidthMm, (s, v) => s.TrayDefaultWidthMm = v, 10, 2000);
            Number(p, "Altura (mm):", s => s.TrayDefaultHeightMm, (s, v) => s.TrayDefaultHeightMm = v, 10, 1000);
            Group(p, "Tamanhos comerciais (largura x altura, um por linha)");
            Lines(p, s => s.TraySizes, (s, v) => s.TraySizes = v);
            return Scroll(p);
        }

        private UIElement TagsTab()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            var p = NewPanel();
            Group(p, "Identificadores de infraestrutura");
            Number(p, "Diâmetro do conduíte a ignorar (mm):", s => s.TagIgnoreConduitDiameterMm,
                (s, v) => s.TagIgnoreConduitDiameterMm = v, 1, 300);
            Number(p, "Largura da bandeja a ignorar (mm):", s => s.TagIgnoreTrayWidthMm,
                (s, v) => s.TagIgnoreTrayWidthMm = v, 1, 2000);
            Number(p, "Altura da bandeja a ignorar (mm):", s => s.TagIgnoreTrayHeightMm,
                (s, v) => s.TagIgnoreTrayHeightMm = v, 1, 1000);
            Note(p, "Tipos de conduíte com diâmetro padrão próprio usam o valor configurado em Conduítes.");
            foreach (var kv in _ctx.TagOptions)
            {
                if (kv.Key != "Eletrodutos" && kv.Key != "Eletrocalhas") continue;
                var combo = new ComboBox();
                combo.Items.Add(new ComboBoxItem { Content = "(padrão do Revit)", Tag = "" });
                foreach (string option in kv.Value) combo.Items.Add(new ComboBoxItem { Content = option, Tag = option });
                Row(p, kv.Key + ":", combo);
                string category = kv.Key;
                _load.Add(s =>
                {
                    string v = s.TagByCategory.TryGetValue(category, out string x) ? x : "";
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) ?? combo.Items[0];
                });
                _save.Add(s =>
                {
                    string v = (string)((ComboBoxItem)combo.SelectedItem).Tag;
                    if (string.IsNullOrEmpty(v)) s.TagByCategory.Remove(category); else s.TagByCategory[category] = v;
                    return null;
                });
            }
            DockPanel.SetDock(p, Dock.Bottom);
            root.Children.Add(p);
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false,
                CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridTextColumn { Header = "Categoria", Binding = new Binding(nameof(TagDeviceRow.Category)),
                IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Família : Tipo", Binding = new Binding(nameof(TagDeviceRow.Family)),
                IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            var picker = new FrameworkElementFactory(typeof(ComboBox));
            picker.SetBinding(ComboBox.ItemsSourceProperty, new Binding(nameof(TagDeviceRow.Options)));
            picker.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(TagDeviceRow.Tag))
                { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Identificador escolhido",
                CellTemplate = new DataTemplate { VisualTree = picker }, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Locar no centro", Binding = new Binding(nameof(TagDeviceRow.Center))
                { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            List<TagDeviceRow> rows = null;
            _load.Add(s =>
            {
                rows = _ctx.DeviceTags.Select(o =>
                {
                    string selected = s.TagByDeviceType.TryGetValue(o.Key, out string saved)
                        ? saved : "(padrão do Revit)";
                    var options = new[] { "(padrão do Revit)" }.Concat(o.Options).ToList();
                    if (!options.Contains(selected)) options.Add(selected);
                    return new TagDeviceRow { Category = o.Category, Family = o.Family, Key = o.Key,
                        Options = options, Tag = selected,
                        Center = s.TagCenterByDeviceType.TryGetValue(o.Key, out bool center) && center };
                }).ToList();
                grid.ItemsSource = rows;
            });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (var row in rows)
                {
                    if (row.Tag == "(padrão do Revit)") s.TagByDeviceType.Remove(row.Key);
                    else if (row.Options.Contains(row.Tag)) s.TagByDeviceType[row.Key] = row.Tag;
                    else return $"Identificador inválido para {row.Family}.";
                    if (row.Center) s.TagCenterByDeviceType[row.Key] = true;
                    else s.TagCenterByDeviceType.Remove(row.Key);
                }
                return null;
            });
            root.Children.Add(grid);
            return root;
        }

        private sealed class TagDeviceRow
        {
            public string Category { get; set; }
            public string Family { get; set; }
            public string Key { get; set; }
            public List<string> Options { get; set; }
            public string Tag { get; set; }
            public bool Center { get; set; }
        }

        private UIElement HeightsTab()
        {
            var root = new DockPanel { Margin = new Thickness(8) };
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                Height = 250 };
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Considerar nível", Binding = new Binding(nameof(LevelRow.Consider)) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding(nameof(LevelRow.Name)), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Elevação (m)", Binding = new Binding(nameof(LevelRow.Elevation)), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Cota de piso (m)", Binding = new Binding(nameof(LevelRow.FloorOffset)) });
            List<LevelRow> rows = null;
            _load.Add(s =>
            {
                rows = _ctx.Levels.Select(l => new LevelRow { Name = l.Name,
                    Elevation = l.ElevationM.ToString("0.00", Br), Consider = s.ConsiderInfraLevel(l.Name),
                    FloorOffset = s.FloorOffset(l.Name).ToString("0.00", Br) }).ToList();
                grid.ItemsSource = rows;
            });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (var row in rows)
                {
                    if (!TryNumber(row.FloorOffset, out double value) || value < -5 || value > 5)
                        return $"Cota de piso inválida para {row.Name}.";
                    s.FloorOffsetByLevel[row.Name] = value;
                    if (row.Consider) s.IgnoredInfraLevels.Remove(row.Name);
                    else if (!s.IgnoredInfraLevels.Contains(row.Name)) s.IgnoredInfraLevels.Add(row.Name);
                }
                return null;
            });
            DockPanel.SetDock(grid, Dock.Top);
            root.Children.Add(grid);
            var p = NewPanel();
            Group(p, "Altura de lançamento da infraestrutura");
            Note(p, "A cota de piso acima é usada para identificar trechos verticais. A altura abaixo é usada para construir conduítes.");
            Number(p, "Altura padrão (m):", s => s.InfraDefaultHeightM, (s, v) => s.InfraDefaultHeightM = v, -5, 20);
            foreach (string level in _ctx.LevelNames)
                DictNumber(p, level, s => s.InfraHeightByLevel, s => s.InfraDefaultHeightM, -5, 20);
            root.Children.Add(Scroll(p));
            return root;
        }

        private sealed class LevelRow
        {
            public bool Consider { get; set; }
            public string Name { get; set; }
            public string Elevation { get; set; }
            public string FloorOffset { get; set; }
        }

        private UIElement UtilityTab()
        {
            var p = NewPanel();
            Check(p, "Registrar concessionária no projeto", s => s.RegisterUtility, (s, v) => s.RegisterUtility = v);
            var utilCombo = Choice(p, "Concessionária de energia:", new[] { "", "AMAZONAS ENERGIA", "CELESC", "CEMIG", "COPEL", "CPFL",
                "EDP", "ENEL CEARÁ", "ENERGISA", "EQUATORIAL", "LIGHT", "NEOENERGIA", "RORAIMA ENERGIA" },
                s => s.Utility, (s, v) => s.Utility = v);
            var supplyCombo = Choice(p, "Sistema de distribuição:", new[] { "220/127V", "230/115V", "380/220V" },
                s => s.SupplySystem, (s, v) => s.SupplySystem = v);
            string[] entries = { "Aéreo", "Subterrâneo de cobre com isolamento em PVC", "Subterrâneo de cobre com isolamento em XLPE" };
            Choice(p, "Padrão de entrada preferido:", entries, s => s.EntranceType, (s, v) => s.EntranceType = v);
            Choice(p, "Padrão de entrada (unidade coletiva):", entries, s => s.EntranceTypeCollective,
                (s, v) => s.EntranceTypeCollective = v);
            Group(p, "Recursos do projeto");
            Note(p, "A criação de definições de voltagem, sistemas de distribuição, fatores de demanda e classificações de cargas requer tabelas próprias da concessionária; esta versão usa os valores de dimensionamento configurados abaixo.");
            foreach (string title in new[] { "Criar definições de voltagem", "Criar sistemas de distribuição",
                "Criar fatores de demanda", "Criar classificação de cargas" })
                p.Children.Add(new CheckBox { Content = title, IsEnabled = false });
            Group(p, "Categorias de atendimento (demanda máx. em kVA;descrição — uma por linha)");
            TextBox catBox = null;
            var presetCombo = new ComboBox { ItemsSource = UtilityPreset.All.Select(x => x.Name).ToList(), Width = 280 };
            var applyBtn = new Button { Content = "Aplicar preset", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            applyBtn.Click += (_, _) =>
            {
                var preset = UtilityPreset.ByName(presetCombo.SelectedItem as string);
                if (preset == null) return;
                utilCombo.SelectedItem = "NEOENERGIA";
                supplyCombo.SelectedItem = preset.SupplySystem;
                catBox.Text = string.Join(Environment.NewLine, preset.EntranceCategories);
            };
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            presetRow.Children.Add(presetCombo);
            presetRow.Children.Add(applyBtn);
            p.Children.Add(presetRow);
            Note(p, "Preset pronto (ex.: Neoenergia Brasília — DIS-NOR-030 REV-07). Ou edite abaixo com a tabela da sua concessionária.");
            Note(p, "Uma faixa por linha, no formato \"limite;descrição\". Use \"kW:\" no limite para enquadrar por carga instalada (kW) e \"kVA:\" para enquadrar por demanda (kVA); sem prefixo, o limite é demanda.");
            catBox = Lines(p, s => s.EntranceCategories, (s, v) => s.EntranceCategories = v);
            Group(p, "Fatores de demanda — iluminação e TUG (por faixa de carga, kVA)");
            Note(p, "Valores típicos de concessionárias brasileiras; confira a norma da sua concessionária.");
            GroupingRow(p, "Fator", new[] { "≤1", "≤2", "≤3", "≤4", "≤5", "≤6", "≤7", "≤8", "≤9", "≤10", ">10" },
                s => s.DemandLightingFactors, (s, v) => s.DemandLightingFactors = v);
            GroupingRow(p, "Fatores de demanda — TUE pela quantidade de aparelhos", new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12+" },
                s => s.DemandTueFactors, (s, v) => s.DemandTueFactors = v);
            Group(p, "Curto-circuito");
            Number(p, "Corrente de curto-circuito presumida na origem (kA):", s => s.SourceShortCircuitKa, (s, v) => s.SourceShortCircuitKa = v, 0.5, 100);
            Number(p, "Capacidade de interrupção dos disjuntores (kA):", s => s.BreakerIcuKa, (s, v) => s.BreakerIcuKa = v, 1, 100);
            return Scroll(p);
        }

        private void DictNumber(Panel p, string key, Func<DimensioningSettings, Dictionary<string, double>> dict,
            Func<DimensioningSettings, double> fallback, double min, double max)
        {
            var box = new TextBox();
            Row(p, key + ":", box);
            _load.Add(s => box.Text = (dict(s).TryGetValue(key, out double v) ? v : fallback(s)).ToString("0.##", Br));
            _save.Add(s =>
            {
                if (!TryNumber(box.Text, out double v) || v < min || v > max) return $"Valor inválido em \"{key}\" (entre {min} e {max}).";
                if (Math.Abs(v - fallback(s)) < 1e-9) dict(s).Remove(key); else dict(s)[key] = v;
                return null;
            });
        }

        private TextBox Lines(Panel p, Func<DimensioningSettings, List<string>> get, Action<DimensioningSettings, List<string>> set)
        {
            var box = new TextBox { AcceptsReturn = true, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
            p.Children.Add(box);
            _load.Add(s => box.Text = string.Join(Environment.NewLine, get(s) ?? new List<string>()));
            _save.Add(s =>
            {
                set(s, box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList());
                return null;
            });
            return box;
        }

        private void ClassesTable(Panel p)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (double w in new[] { 3.0, 4.0, 1.0, 1.0, 1.0, 1.5, 1.5 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            string[] headers = { "Classe", "Palavras na descrição (;)", "Curva", "Neutro", "Terra", "Seção mín. mm²", "Disj. mín. A" };
            g.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < headers.Length; i++)
            {
                var h = new TextBlock { Text = headers[i], FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(h, i);
                g.Children.Add(h);
            }
            const int rows = 8;
            var name = new TextBox[rows]; var keys = new TextBox[rows]; var curve = new ComboBox[rows]; var neutral = new CheckBox[rows]; var ground = new CheckBox[rows];
            var minSection = new TextBox[rows]; var minBreaker = new TextBox[rows];
            for (int r = 0; r < rows; r++)
            {
                g.RowDefinitions.Add(new RowDefinition());
                name[r] = new TextBox { Margin = new Thickness(1) };
                keys[r] = new TextBox { Margin = new Thickness(1) };
                curve[r] = new ComboBox { Margin = new Thickness(1) };
                foreach (string c in new[] { "B", "C", "D" }) curve[r].Items.Add(c);
                neutral[r] = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ground[r] = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                minSection[r] = new TextBox { Margin = new Thickness(1) };
                minBreaker[r] = new TextBox { Margin = new Thickness(1) };
                UIElement[] cells = { name[r], keys[r], curve[r], neutral[r], ground[r], minSection[r], minBreaker[r] };
                for (int c = 0; c < cells.Length; c++)
                {
                    Grid.SetRow(cells[c], r + 1);
                    Grid.SetColumn(cells[c], c);
                    g.Children.Add(cells[c]);
                }
            }
            p.Children.Add(g);
            _load.Add(s =>
            {
                for (int r = 0; r < rows; r++)
                {
                    CircuitClassRule rule = r < s.CircuitClasses.Count ? s.CircuitClasses[r] : null;
                    name[r].Text = rule?.Name ?? "";
                    keys[r].Text = rule?.Keywords ?? "";
                    curve[r].SelectedItem = rule?.Curve ?? "C";
                    neutral[r].IsChecked = rule?.Neutral ?? true;
                    ground[r].IsChecked = rule?.Ground ?? true;
                    minSection[r].Text = rule?.MinimumSectionMm2 > 0 ? rule.MinimumSectionMm2.ToString(Br) : "";
                    minBreaker[r].Text = rule?.MinimumBreakerA > 0 ? rule.MinimumBreakerA.ToString(Br) : "";
                }
            });
            _save.Add(s =>
            {
                var list = new List<CircuitClassRule>();
                for (int r = 0; r < rows; r++)
                {
                    if (string.IsNullOrWhiteSpace(name[r].Text)) continue;
                    if (!double.TryParse(string.IsNullOrWhiteSpace(minSection[r].Text) ? "0" : minSection[r].Text, System.Globalization.NumberStyles.Float, Br, out double section) ||
                        !int.TryParse(string.IsNullOrWhiteSpace(minBreaker[r].Text) ? "0" : minBreaker[r].Text, out int breaker))
                        return "Mínimos de seção e disjuntor devem ser números válidos.";
                    list.Add(new CircuitClassRule
                    {
                        Name = name[r].Text.Trim(),
                        Keywords = keys[r].Text.Trim(),
                        Curve = curve[r].SelectedItem as string ?? "C",
                        Neutral = neutral[r].IsChecked == true,
                        Ground = ground[r].IsChecked == true,
                        MinimumSectionMm2 = section,
                        MinimumBreakerA = breaker,
                    });
                }
                s.CircuitClasses = list;
                return null;
            });
        }

        private void SizingRulesTable(Panel p)
        {
            var rows = new ObservableCollection<SizingRule>();
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                Height = 180, ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding(nameof(SizingRule.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Disj. mín. (A)", ItemsSource = Nbr5410.BreakerRatings,
                SelectedItemBinding = new Binding(nameof(SizingRule.MinimumBreakerA)) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Seção mín. (mm²)", ItemsSource = Nbr5410.Sections,
                SelectedItemBinding = new Binding(nameof(SizingRule.MinimumSectionMm2)) });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "OU (desmarcado = E)", Binding = new Binding(nameof(SizingRule.MatchAny)) });
            p.Children.Add(grid);
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            Button Action(string title, Action action)
            {
                var button = new Button { Content = title, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(8, 3, 8, 3) };
                button.Click += (_, _) => action();
                bar.Children.Add(button);
                return button;
            }
            Action("Adicionar", () => { var rule = new SizingRule { Conditions = new() { new SizingCondition() } }; rows.Add(rule); grid.SelectedItem = rule; });
            Action("Duplicar", () =>
            {
                if (grid.SelectedItem is not SizingRule selected) return;
                var copy = new SizingRule { Name = selected.Name + " (cópia)", MinimumBreakerA = selected.MinimumBreakerA,
                    MinimumSectionMm2 = selected.MinimumSectionMm2, MatchAny = selected.MatchAny,
                    Conditions = selected.Conditions.Select(c => new SizingCondition { Type = c.Type, Operator = c.Operator, Value = c.Value }).ToList() };
                rows.Add(copy); grid.SelectedItem = copy;
            });
            Action("Remover", () => { if (grid.SelectedItem is SizingRule selected) rows.Remove(selected); });
            Action("Editar condições", () => { if (grid.SelectedItem is SizingRule selected) EditSizingConditions(selected); });
            p.Children.Add(bar);
            _load.Add(s => { rows.Clear(); foreach (var rule in s.SizingRules) rows.Add(rule); });
            _save.Add(s =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                s.SizingRules = rows.ToList();
                return null;
            });
        }

        private void EditSizingConditions(SizingRule rule)
        {
            var rows = new ObservableCollection<SizingCondition>(rule.Conditions.Select(c =>
                new SizingCondition { Type = c.Type, Operator = c.Operator, Value = c.Value }));
            var dialog = new Window { Title = "Condições de dimensionamento", Owner = this,
                Width = 640, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var root = new DockPanel { Margin = new Thickness(8) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            var add = new Button { Content = "Adicionar condição", Margin = new Thickness(0, 0, 8, 0) };
            add.Click += (_, _) => rows.Add(new SizingCondition());
            var remove = new Button { Content = "Remover selecionada" };
            bar.Children.Add(add); bar.Children.Add(remove);
            DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(4) };
            var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(4) };
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Tipo de regra", ItemsSource = new[]
                { "Potência aparente (VA)", "Corrente (A)", "Nº fases", "Descrição da carga", "Família específica" },
                SelectedItemBinding = new Binding(nameof(SizingCondition.Type)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Operador", ItemsSource = new[]
                { "Igual a", "Não é igual a", "Maior que", "Maior ou igual a", "Menor que", "Menor ou igual a", "Contém", "Não contém" },
                SelectedItemBinding = new Binding(nameof(SizingCondition.Operator)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Valor", Binding = new Binding(nameof(SizingCondition.Value)),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            remove.Click += (_, _) => { if (grid.SelectedItem is SizingCondition selected) rows.Remove(selected); };
            ok.Click += (_, _) =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                if (rows.Count == 0) { MessageBox.Show(dialog, "Adicione ao menos uma condição."); return; }
                rule.Conditions = rows.ToList();
                dialog.DialogResult = true;
            };
            root.Children.Add(grid); dialog.Content = root; dialog.ShowDialog();
        }

        private UIElement ExchangeTab()
        {
            var p = NewPanel();
            if (_projectOption != null)
            {
                Group(p, "Onde guardar");
                _projectCheck = new CheckBox
                {
                    Content = new TextBlock { Text = "Guardar estas configurações dentro deste projeto (.rvt)", TextWrapping = TextWrapping.Wrap },
                    IsChecked = _projectOption == true,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                p.Children.Add(_projectCheck);
                Note(p, "Marcado: o projeto passa a usar as próprias configurações, em qualquer computador que abrir o arquivo. " +
                        "Desmarcado: o projeto usa as configurações deste computador.");
            }

            Group(p, "Exportar configurações");
            Note(p, "Salva todas as configurações desta janela num arquivo .json (inclusive alterações ainda não confirmadas com OK).");
            var export = new Button { Content = "Exportar...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            export.Click += (_, _) =>
            {
                DimensioningSettings temp = Settings.Clone();
                string error = Read(temp);
                if (error != null) { Warn(error); return; }
                var d = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "CircuitoLivre_Configuracoes.json" };
                if (d.ShowDialog(this) != true) return;
                if (temp.SaveTo(d.FileName)) MessageBox.Show(this, "Configurações exportadas.", Title);
                else Warn("Não foi possível salvar o arquivo.");
            };
            p.Children.Add(export);

            Group(p, "Importar configurações");
            Note(p, "Preenche esta janela com as configurações de um arquivo .json. Nada é gravado até clicar em OK.");
            var import = new Button { Content = "Importar...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            import.Click += (_, _) =>
            {
                var d = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
                if (d.ShowDialog(this) != true) return;
                DimensioningSettings loaded = DimensioningSettings.LoadFrom(d.FileName);
                if (loaded == null) { Warn("Arquivo inválido."); return; }
                Settings = loaded;
                Fill(loaded);
            };
            p.Children.Add(import);
            return Scroll(p);
        }

        // ------------------------------------------------------------------ carregar / gravar

        private void Fill(DimensioningSettings s)
        {
            Settings = s.Clone();
            foreach (var a in _load) a(Settings);
        }

        private string Read(DimensioningSettings s)
        {
            foreach (var f in _save)
            {
                string error = f(s);
                if (error != null) return error;
            }
            try { s.ValidateForSizing(); }
            catch (ArgumentException ex) { return ex.Message; }
            return null;
        }

        private bool Commit()
        {
            DimensioningSettings s = Settings;
            string error = Read(s);
            if (error != null) { Warn(error); return false; }
            Settings = s.Clone();
            return true;
        }

        private void Warn(string text) => MessageBox.Show(this, text, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

        // ------------------------------------------------------------------ construtores de campos

        private static TabItem Tab(string header, UIElement content) => new TabItem { Header = header, Content = content, Padding = new Thickness(10, 4, 10, 4) };

        private static StackPanel NewPanel() => new StackPanel { Margin = new Thickness(12, 6, 12, 6) };

        private static ScrollViewer Scroll(UIElement e) => new ScrollViewer { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private static void Group(Panel p, string title) =>
            p.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });

        private static void Note(Panel p, string text) =>
            p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 4) });

        private static Grid Row(Panel p, string label, UIElement field)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(field, 1);
            g.Children.Add(t);
            g.Children.Add(field);
            p.Children.Add(g);
            return g;
        }

        private static bool TryNumber(string text, out double v) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

        private void Number(Panel p, string label, Func<DimensioningSettings, double> get, Action<DimensioningSettings, double> set, double min, double max)
        {
            var box = new TextBox();
            Row(p, label, box);
            _load.Add(s => box.Text = get(s).ToString("0.#####", Br));
            _save.Add(s =>
            {
                if (!TryNumber(box.Text, out double v) || v < min || v > max) return $"Valor inválido em \"{label.TrimEnd(':')}\" (entre {min} e {max}).";
                set(s, v);
                return null;
            });
        }

        private void Integer(Panel p, string label, Func<DimensioningSettings, int> get, Action<DimensioningSettings, int> set, int min, int max)
        {
            var box = new TextBox();
            Row(p, label, box);
            _load.Add(s => box.Text = get(s).ToString(Br));
            _save.Add(s =>
            {
                if (!int.TryParse(box.Text.Trim(), out int v) || v < min || v > max) return $"Valor inválido em \"{label.TrimEnd(':')}\".";
                set(s, v);
                return null;
            });
        }

        private void Text(Panel p, string label, Func<DimensioningSettings, string> get, Action<DimensioningSettings, string> set)
        {
            var box = new TextBox();
            Row(p, label, box);
            _load.Add(s => box.Text = get(s) ?? "");
            _save.Add(s => { set(s, box.Text.Trim()); return null; });
        }

        private ComboBox Choice(Panel p, string label, IEnumerable<string> options,
            Func<DimensioningSettings, string> get, Action<DimensioningSettings, string> set)
        {
            var values = options.ToList();
            var combo = new ComboBox { ItemsSource = values };
            Row(p, label, combo);
            _load.Add(s =>
            {
                string selected = get(s);
                if (!string.IsNullOrWhiteSpace(selected) && !values.Contains(selected)) values.Add(selected);
                combo.ItemsSource = null;
                combo.ItemsSource = values;
                combo.SelectedItem = values.Contains(selected) ? selected : values.First();
            });
            _save.Add(s => { set(s, (string)combo.SelectedItem); return null; });
            return combo;
        }

        private void Check(Panel p, string label, Func<DimensioningSettings, bool> get, Action<DimensioningSettings, bool> set)
        {
            var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 3, 0, 3) };
            p.Children.Add(box);
            _load.Add(s => box.IsChecked = get(s));
            _save.Add(s => { set(s, box.IsChecked == true); return null; });
        }

        private void Radio(Panel p, string group, string label, Func<DimensioningSettings, bool> isSelected, Action<DimensioningSettings> select)
        {
            var radio = new RadioButton { GroupName = group, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 3, 0, 3) };
            p.Children.Add(radio);
            _load.Add(s => radio.IsChecked = isSelected(s));
            _save.Add(s => { if (radio.IsChecked == true) select(s); return null; });
        }

        private void SectionCombo(Panel p, string label, Func<DimensioningSettings, double> get, Action<DimensioningSettings, double> set, bool allowNone)
        {
            var combo = new ComboBox();
            if (allowNone) combo.Items.Add(new ComboBoxItem { Content = "(nenhuma)", Tag = 0.0 });
            foreach (double x in Nbr5410.Sections.Where(x => x >= 1))
                combo.Items.Add(new ComboBoxItem { Content = Nbr5410.FormatSection(x), Tag = x });
            Row(p, label, combo);
            _load.Add(s =>
            {
                double v = get(s);
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((double)i.Tag - v) < 1e-9)
                                     ?? combo.Items.Cast<ComboBoxItem>().First();
            });
            _save.Add(s => { set(s, (double)((ComboBoxItem)combo.SelectedItem).Tag); return null; });
        }

        private void GroupingRow(Panel p, string title, string[] headers, Func<DimensioningSettings, double[]> get, Action<DimensioningSettings, double[]> set)
        {
            Group(p, title);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            var boxes = new TextBox[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var h = new TextBlock { Text = headers[i], HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(h, i);
                g.Children.Add(h);
                boxes[i] = new TextBox { Margin = new Thickness(1), TextAlignment = TextAlignment.Center };
                Grid.SetColumn(boxes[i], i);
                Grid.SetRow(boxes[i], 1);
                g.Children.Add(boxes[i]);
            }
            p.Children.Add(g);
            _load.Add(s =>
            {
                double[] v = get(s);
                for (int i = 0; i < boxes.Length; i++) boxes[i].Text = i < v.Length ? v[i].ToString("0.00", Br) : "";
            });
            _save.Add(s =>
            {
                var v = new double[boxes.Length];
                for (int i = 0; i < boxes.Length; i++)
                    if (!TryNumber(boxes[i].Text, out v[i]) || v[i] <= 0 || v[i] > 1) return $"Fator inválido em \"{title}\" (coluna {headers[i]}).";
                set(s, v);
                return null;
            });
        }
    }

    /// <summary>Listas do projeto aberto usadas pela janela de configurações.</summary>
    public class SettingsContext
    {
        public List<string> ConduitTypes { get; set; } = new();
        public List<string> LevelNames { get; set; } = new();
        public List<LevelOption> Levels { get; set; } = new();
        public Dictionary<string, List<string>> PlanViewsByLevel { get; set; } = new();
        public List<string> CircuitLoadNames { get; set; } = new();
        public Dictionary<string, List<string>> TagOptions { get; set; } = new();
        public List<TagDeviceOption> DeviceTags { get; set; } = new();
    }

    public class TagDeviceOption
    {
        public string Category { get; set; }
        public string Family { get; set; }
        public string Key { get; set; }
        public List<string> Options { get; set; }
    }

    public class LevelOption
    {
        public string Name { get; set; }
        public double ElevationM { get; set; }
    }
}
