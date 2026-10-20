using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using RafaelElectrical.Services;

namespace RafaelElectrical.Views
{
    public partial class LoadReportWindow : Window
    {
        private const string AllPanels = "(todos)";
        private readonly List<CircuitRow> _rows;

        /// <summary>Circuito escolhido em "Selecionar no modelo" (ou duplo clique).</summary>
        public ElementId SelectedCircuitId { get; private set; }

        public LoadReportWindow(List<CircuitRow> rows, string documentTitle)
        {
            InitializeComponent();
            Language = XmlLanguage.GetLanguage("pt-BR");
            Title = $"Relatório de cargas — {documentTitle}";
            _rows = rows;

            var panels = new List<string> { AllPanels };
            panels.AddRange(rows.Select(r => r.Panel).Distinct().OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase));
            PanelCombo.ItemsSource = panels;
            PanelCombo.SelectedIndex = 0;
        }

        private IEnumerable<CircuitRow> Visible =>
            PanelCombo.SelectedItem is string p && p != AllPanels ? _rows.Where(r => r.Panel == p) : _rows;

        private void PanelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            List<CircuitRow> visible = Visible.ToList();
            CircuitGrid.ItemsSource = visible;

            CultureInfo br = CultureInfo.GetCultureInfo("pt-BR");
            double va = visible.Sum(r => r.ApparentLoadVA);
            double w = visible.Sum(r => r.TrueLoadW);
            TotalsText.Text = $"{visible.Count} circuito(s)  |  Total: {va.ToString("N0", br)} VA  /  {w.ToString("N0", br)} W";
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Exportar relatório de cargas",
                Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                FileName = "Relatorio_Cargas.xlsx",
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                CircuitReportService.ExportCsv(Visible, dialog.FileName);
                MessageBox.Show(this, "Relatório exportado com sucesso.", "Relatório de cargas", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível salvar o arquivo:\n{ex.Message}", "Relatório de cargas", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Select_Click(object sender, RoutedEventArgs e) => SelectCurrent();

        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SelectCurrent();

        private void SelectCurrent()
        {
            if (CircuitGrid.SelectedItem is not CircuitRow row) return;
            SelectedCircuitId = row.Id;
            Close();
        }
    }
}
