using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Microsoft.Win32;
using RafaelElectrical.Services;

namespace RafaelElectrical.Views
{
    public partial class DimensionWindow : Window
    {
        private readonly List<CircuitInput> _inputs;

        public DimensioningSettings Settings { get; private set; }
        public List<SizingResult> Results { get; private set; } = new List<SizingResult>();
        public bool ApplyRequested { get; private set; }

        public DimensionWindow(List<CircuitInput> inputs, DimensioningSettings settings, string scope)
        {
            InitializeComponent();
            Language = XmlLanguage.GetLanguage("pt-BR");
            Title = $"Dimensionar instalação (NBR 5410) — {scope}";
            _inputs = inputs;
            Settings = settings;
            LoadSettings(settings);
            Calculate();
        }

        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        private void LoadSettings(DimensioningSettings s)
        {
            SelectByTag(MethodCombo, s.Method.ToString());
            SelectByTag(InsulationCombo, s.Insulation.ToString());
            TempBox.Text = s.TemperatureC.ToString(Br);
            GroupedBox.Text = s.GroupedCircuits.ToString(Br);
            DropBox.Text = s.MaxVoltageDropPercent.ToString(Br);
            RhoBox.Text = s.Resistivity.ToString("0.#####", Br);
            MinLightBox.Text = s.MinSectionLighting.ToString(Br);
            MinPowerBox.Text = s.MinSectionPower.ToString(Br);
            RatingCheck.IsChecked = s.UpdateRevitRating;
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
                if ((string)item.Tag == tag) { combo.SelectedItem = item; return; }
            combo.SelectedIndex = 0;
        }

        private bool ReadSettings(out string error)
        {
            error = null;
            // parte das configurações completas (as da janela Configurações são preservadas)
            DimensioningSettings s = Settings.Clone();
            s.Method = Enum.Parse<InstallMethod>((string)((ComboBoxItem)MethodCombo.SelectedItem).Tag);
            s.Insulation = Enum.Parse<Insulation>((string)((ComboBoxItem)InsulationCombo.SelectedItem).Tag);
            s.UpdateRevitRating = RatingCheck.IsChecked == true;

            if (!TryNumber(TempBox.Text, out double temp) || temp < 0 || temp > 80) error = "Temperatura inválida.";
            else if (!int.TryParse(GroupedBox.Text, out int group) || group < 1) error = "Quantidade de circuitos agrupados inválida.";
            else if (!TryNumber(DropBox.Text, out double drop) || drop <= 0) error = "Queda de tensão inválida.";
            else if (!TryNumber(RhoBox.Text, out double rho) || rho <= 0) error = "Resistividade inválida.";
            else if (!TryNumber(MinLightBox.Text, out double minL) || !Nbr5410.Sections.Contains(minL)) error = "Seção mínima de iluminação inválida.";
            else if (!TryNumber(MinPowerBox.Text, out double minP) || !Nbr5410.Sections.Contains(minP)) error = "Seção mínima de força inválida.";
            else
            {
                s.TemperatureC = temp;
                s.GroupedCircuits = group;
                s.MaxVoltageDropPercent = drop;
                s.Resistivity = rho;
                s.MinSectionLighting = minL;
                s.MinSectionPower = minP;
                Settings = s;
                return true;
            }
            return false;
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private void Calculate()
        {
            Results = SizeAll(_inputs, Settings, out int totalFail);
            ResultGrid.ItemsSource = Results;

            int ok = Results.Count(r => r.Ok);
            int fail = Results.Count - ok;
            int noLength = Results.Count(r => r.Ok && r.LengthM <= 0);
            SummaryText.Text = $"{Results.Count} circuito(s): {ok} dimensionado(s)" +
                               (fail > 0 ? $", {fail} com problema" : "") +
                               (noLength > 0 ? $" — {noLength} sem comprimento (queda não verificada)" : "") +
                               $".  FCT = {Nbr5410.TemperatureFactor(Settings.Method, Settings.Insulation, Settings.TemperatureC):0.00}" +
                               $"  FCA padrão = {Settings.GroupingFactor(GroupingKind.Closed, Settings.GroupedCircuits):0.00}" +
                               (Settings.CheckTotalVoltageDrop ? $"  — ΔV total ≤ {Settings.TotalVoltageDropPercent:0.#} %" +
                                   (totalFail > 0 ? $" ({totalFail} acima)" : "") : "");
        }

        /// <summary>
        /// Dimensiona todos os circuitos. Com a verificação de queda total, acumula a queda dos alimentadores
        /// (circuito que alimenta o quadro) e redimensiona os circuitos cuja queda total passa do limite.
        /// </summary>
        public static List<SizingResult> SizeAll(List<CircuitInput> inputs, DimensioningSettings s, out int totalFail)
        {
            foreach (CircuitInput i in inputs) { i.UpstreamDropPercent = 0; i.DropLimitOverride = null; }
            List<SizingResult> results = inputs.Select(i => CircuitSizer.Size(i, s)).ToList();
            totalFail = 0;
            if (!s.CheckTotalVoltageDrop) return results;

            for (int pass = 0; pass < 4; pass++)
            {
                // queda de cada alimentador por quadro alimentado
                var feeder = results.Where(r => r.Ok && !string.IsNullOrEmpty(r.Input.FeedsPanel))
                    .GroupBy(r => r.Input.FeedsPanel, StringComparer.CurrentCultureIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);

                double Upstream(string panel, int depth)
                {
                    if (depth > 20 || panel == null || !feeder.TryGetValue(panel, out SizingResult f)) return 0;
                    return f.VoltageDropPercent + Upstream(f.Input.Panel, depth + 1);
                }

                bool changed = false;
                foreach (CircuitInput i in inputs)
                {
                    double up = Math.Round(Upstream(i.Panel, 0), 3);
                    double allowed = Math.Max(0.1, s.TotalVoltageDropPercent - up);
                    double? limit = allowed < s.MaxVoltageDropPercent ? allowed : (double?)null;
                    if (Math.Abs(up - i.UpstreamDropPercent) > 1e-6 || limit != i.DropLimitOverride) changed = true;
                    i.UpstreamDropPercent = up;
                    i.DropLimitOverride = limit;
                }
                results = inputs.Select(i => CircuitSizer.Size(i, s)).ToList();
                if (!changed) break;
            }

            foreach (SizingResult r in results.Where(r => r.Ok && r.TotalDropPercent > s.TotalVoltageDropPercent + 1e-6))
            {
                r.Status = $"Queda total {r.TotalDropPercent:0.00} % > {s.TotalVoltageDropPercent:0.#} %";
                totalFail++;
            }
            return results;
        }

        private void Calculate_Click(object sender, RoutedEventArgs e)
        {
            if (!ReadSettings(out string error))
            {
                MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Calculate();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (!ReadSettings(out string error))
            {
                MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Calculate();
            Settings.Save();
            ApplyRequested = true;
            DialogResult = true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Exportar relatório de dimensionamento",
                Filter = "CSV (Excel) (*.csv)|*.csv",
                FileName = "Relatorio_Dimensionamento.csv",
            };
            if (dialog.ShowDialog(this) != true) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Método {Settings.Method};Isolação {Settings.Insulation};Temperatura {Settings.TemperatureC.ToString(Br)} °C;" +
                          $"Agrupados {Settings.GroupedCircuits};Queda máx. {Settings.MaxVoltageDropPercent.ToString(Br)} %");
            sb.AppendLine("Quadro;Circuito;Descrição;Tensão (V);Polos;Ib (A);Comprimento (m);Agrupamento;Seção (mm²);PE (mm²);Disjuntor (A);Iz (A);Queda (%);Queda total (%);Critério;Fiação;Situação");
            foreach (SizingResult r in Results)
            {
                sb.AppendLine(string.Join(";",
                    Esc(r.Panel), Esc(r.Number), Esc(r.LoadName),
                    r.VoltageV.ToString("0", Br), r.Poles.ToString(Br), r.CurrentA.ToString("0.00", Br),
                    r.LengthM.ToString("0.0", Br), r.Grouping,
                    r.SectionMm2 > 0 ? Nbr5410.FormatSection(r.SectionMm2) : "",
                    r.ProtectionMm2 > 0 ? Nbr5410.FormatSection(r.ProtectionMm2) : "",
                    r.BreakerA > 0 ? r.BreakerA.ToString(Br) : "",
                    r.IzA.ToString("0.0", Br), r.VoltageDropPercent.ToString("0.00", Br), r.TotalDropPercent.ToString("0.00", Br),
                    Esc(r.Criterion), Esc(r.Wiring), Esc(r.Status)));
            }

            try
            {
                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show(this, "Relatório exportado.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível salvar:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string Esc(string v)
        {
            v ??= "";
            return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}
