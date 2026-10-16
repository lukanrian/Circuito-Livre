using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RafaelElectrical.Services;

namespace RafaelElectrical.Views
{
    /// <summary>
    /// Janela de configurações do RafaelElectrical (montada em código): geral, fiação, agrupamento, ocupação,
    /// dimensionamento, telecom, documentação e exportação/importação em JSON.
    /// </summary>
    public class SettingsWindow : Window
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public DimensioningSettings Settings { get; private set; }

        private readonly List<Action<DimensioningSettings>> _load = new();
        private readonly List<Func<DimensioningSettings, string>> _save = new();

        public SettingsWindow(DimensioningSettings settings)
        {
            Title = "RafaelElectrical — Configurações";
            Width = 760;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Settings = settings.Clone();

            var tabs = new TabControl { TabStripPlacement = Dock.Left, Margin = new Thickness(8) };
            tabs.Items.Add(Tab("Geral", GeneralTab()));
            tabs.Items.Add(Tab("Fiação", WiringTab()));
            tabs.Items.Add(Tab("Agrupamento", GroupingTab()));
            tabs.Items.Add(Tab("Ocupação", OccupancyTab()));
            tabs.Items.Add(Tab("Dimensionamento", SizingTab()));
            tabs.Items.Add(Tab("Telecom", TelecomTab()));
            tabs.Items.Add(Tab("Documentação", DocumentationTab()));
            tabs.Items.Add(Tab("Intercâmbio", ExchangeTab()));

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
            Number(p, "Temperatura ambiente (°C):", s => s.TemperatureC, (s, v) => s.TemperatureC = v, 0, 80);
            SectionCombo(p, "Seção a omitir nos identificadores de fiação:", s => s.OmitSectionInTags, (s, v) => s.OmitSectionInTags = v, allowNone: true);
            SectionCombo(p, "Seção máxima de dimensionamento (mm²):", s => s.MaxSection, (s, v) => s.MaxSection = v, allowNone: false);
            Group(p, "Seções mínimas (Tabela 47)");
            SectionCombo(p, "Circuitos de iluminação (mm²):", s => s.MinSectionLighting, (s, v) => s.MinSectionLighting = v, allowNone: false);
            SectionCombo(p, "Circuitos de força (mm²):", s => s.MinSectionPower, (s, v) => s.MinSectionPower = v, allowNone: false);
            return Scroll(p);
        }

        private UIElement WiringTab()
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
            Group(p, "Seções do neutro e do terra");
            Check(p, "Neutro reduzido nos circuitos trifásicos com fase > 25 mm² (Tabela 48 — só se a 3ª harmônica e o desequilíbrio permitirem)",
                s => s.ReducedNeutral, (s, v) => s.ReducedNeutral = v);
            Note(p, "O condutor de proteção segue a Tabela 58 (até 16 mm² igual à fase; 25–35 mm² → 16 mm²; acima, metade da fase).");
            return Scroll(p);
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
            Group(p, "Revit");
            Check(p, "Atualizar o disjuntor nativo (Classificação) do circuito", s => s.UpdateRevitRating, (s, v) => s.UpdateRevitRating = v);
            return Scroll(p);
        }

        private UIElement TelecomTab()
        {
            var p = NewPanel();
            Group(p, "Cabo utilizado por sistema (relatório de fiação)");
            Text(p, "Dados:", s => s.CableData, (s, v) => s.CableData = v);
            Text(p, "Telefone:", s => s.CableTelephone, (s, v) => s.CableTelephone = v);
            Text(p, "Comunicações:", s => s.CableCommunication, (s, v) => s.CableCommunication = v);
            Text(p, "Segurança:", s => s.CableSecurity, (s, v) => s.CableSecurity = v);
            Group(p, "Ocupação");
            Number(p, "Diâmetro externo médio do cabo de telecom (mm):", s => s.TelecomCableDiameterMm, (s, v) => s.TelecomCableDiameterMm = v, 1, 50);
            return Scroll(p);
        }

        private UIElement DocumentationTab()
        {
            var p = NewPanel();
            Group(p, "Diagrama unifilar");
            Check(p, "Desenhar DPS no barramento do quadro", s => s.DiagramUseDps, (s, v) => s.DiagramUseDps = v);
            Text(p, "Texto do DPS:", s => s.DiagramDpsText, (s, v) => s.DiagramDpsText = v);
            return Scroll(p);
        }

        private UIElement ExchangeTab()
        {
            var p = NewPanel();
            Group(p, "Exportar configurações");
            Note(p, "Salva todas as configurações desta janela num arquivo .json (inclusive alterações ainda não confirmadas com OK).");
            var export = new Button { Content = "Exportar...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            export.Click += (_, _) =>
            {
                DimensioningSettings temp = Settings.Clone();
                string error = Read(temp);
                if (error != null) { Warn(error); return; }
                var d = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "RafaelElectrical_Configuracoes.json" };
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
            foreach (var a in _load) a(s);
        }

        private string Read(DimensioningSettings s)
        {
            foreach (var f in _save)
            {
                string error = f(s);
                if (error != null) return error;
            }
            return null;
        }

        private bool Commit()
        {
            DimensioningSettings s = Settings.Clone();
            string error = Read(s);
            if (error != null) { Warn(error); return false; }
            Settings = s;
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
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

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
}
