using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Microsoft.Win32;
using RafaelElectrical.Services;

namespace RafaelElectrical.Views
{
    /// <summary>Uma origem de níveis: um vínculo carregado ou um arquivo IFC.</summary>
    public class LevelSource
    {
        public LevelSource(string label, List<SourceLevel> levels)
        {
            Label = label;
            Levels = levels;
        }

        public string Label { get; }
        public List<SourceLevel> Levels { get; }
    }

    /// <summary>Nível que já existe no projeto atual (elevação em metros).</summary>
    public class ExistingLevel
    {
        public ExistingLevel(string name, double elevationM)
        {
            Name = name;
            ElevationM = elevationM;
        }

        public string Name { get; }
        public double ElevationM { get; }
    }

    public class LevelRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _targetName;

        public string SourceName { get; set; }
        public double ElevationM { get; set; }
        public string Status { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnChanged(nameof(IsSelected)); }
        }

        public string TargetName
        {
            get => _targetName;
            set { _targetName = value; OnChanged(nameof(TargetName)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class CopyLevelsWindow : Window
    {
        private const double ToleranceM = 0.001;
        private readonly List<ExistingLevel> _existing;
        private List<LevelRow> _rows = new List<LevelRow>();
        private bool _loaded;

        public List<LevelRow> SelectedRows => _rows.Where(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.TargetName)).ToList();
        public List<CheckItem> ViewModels { get; }
        public List<object> SelectedViewModels => ViewModels.Where(v => v.IsChecked).Select(v => v.Tag).ToList();
        public bool UpdateExisting => UpdateExistingCheck.IsChecked == true;
        public bool ViewsOnAllLevels => AllLevelsCheck.IsChecked == true;
        public bool ReplaceExisting => ReplaceCheck.IsChecked == true;
        public List<LevelRow> AllRows => _rows.Where(r => !string.IsNullOrWhiteSpace(r.TargetName)).ToList();

        public CopyLevelsWindow(List<LevelSource> sources, List<ExistingLevel> existing, List<CheckItem> viewModels)
        {
            InitializeComponent();
            // ocupa a maior parte da tela (sem passar dela)
            Rect area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width * 0.92);
            Height = Math.Min(Height, area.Height * 0.92);
            ViewModels = viewModels;
            ViewModelsList.ItemsSource = viewModels;
            Language = XmlLanguage.GetLanguage("pt-BR");
            _existing = existing;

            SourceCombo.ItemsSource = sources;
            _loaded = true;

            if (sources.Count > 0)
                SourceCombo.SelectedIndex = 0;
            else
                InfoText.Text = "Nenhum vínculo Revit/IFC carregado no projeto. Use \"Abrir arquivo IFC...\" para ler um .ifc.";
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Rebuild();

        private void Options_Changed(object sender, TextChangedEventArgs e) => Rebuild();

        private void Replace_Changed(object sender, RoutedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (!_loaded) return;
            if (SourceCombo.SelectedItem is not LevelSource source)
            {
                LevelsGrid.ItemsSource = null;
                return;
            }

            string prefix = PrefixBox.Text ?? "";
            double offset = ParseOffset();
            bool replace = ReplaceCheck.IsChecked == true;
            var targetNames = new HashSet<string>(source.Levels.Select(l => prefix + l.Name), StringComparer.CurrentCultureIgnoreCase);
            // na substituição, só contam os níveis existentes que serão mantidos (mesmo nome de um nível da origem)
            List<ExistingLevel> existing = replace ? _existing.Where(x => targetNames.Contains(x.Name)).ToList() : _existing;

            _rows = source.Levels.Select(l =>
            {
                double elevation = l.ElevationM + offset;
                string target = prefix + l.Name;
                var row = new LevelRow { SourceName = l.Name, ElevationM = elevation, TargetName = target };

                ExistingLevel sameName = existing.FirstOrDefault(x => string.Equals(x.Name, target, StringComparison.CurrentCultureIgnoreCase));
                ExistingLevel sameElevation = existing.FirstOrDefault(x => Math.Abs(x.ElevationM - elevation) < ToleranceM);

                if (sameName != null && Math.Abs(sameName.ElevationM - elevation) < ToleranceM)
                {
                    row.Status = "Já existe";
                    row.IsSelected = false;
                }
                else if (sameName != null)
                {
                    row.Status = $"Já existe com elevação {sameName.ElevationM:N3} m";
                    row.IsSelected = false;
                }
                else if (sameElevation != null)
                {
                    row.Status = $"Já há nível nesta cota: \"{sameElevation.Name}\"";
                    row.IsSelected = false;
                }
                else
                {
                    row.Status = "Novo";
                    row.IsSelected = true;
                }
                return row;
            }).ToList();

            LevelsGrid.ItemsSource = _rows;
            int news = _rows.Count(r => r.Status == "Novo");
            int removed = replace ? _existing.Count(x => !targetNames.Contains(x.Name)) : 0;
            InfoText.Text = (replace ? $"Substituição: {removed} nível(is) existente(s) serão apagados. " : "") + $"{_rows.Count} nível(is) na origem — {news} novo(s). Marque os que deseja criar (ou que já existem e devem receber vistas) e, se quiser, edite os nomes.";
        }

        private double ParseOffset()
        {
            string t = (OffsetBox.Text ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private void OpenIfc_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Selecionar arquivo IFC",
                Filter = "IFC (*.ifc)|*.ifc",
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                List<SourceLevel> levels = IfcLevelReader.Read(dialog.FileName);
                if (levels.Count == 0)
                {
                    MessageBox.Show(this, "Nenhum pavimento (IfcBuildingStorey) encontrado neste arquivo.", Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var sources = ((IEnumerable<LevelSource>)SourceCombo.ItemsSource).ToList();
                var source = new LevelSource($"Arquivo IFC: {System.IO.Path.GetFileName(dialog.FileName)}", levels);
                sources.Add(source);
                SourceCombo.ItemsSource = sources;
                SourceCombo.SelectedItem = source;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível ler o IFC:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = null;
            }
        }

        private void CheckAll_Click(object sender, RoutedEventArgs e) => _rows.ForEach(r => r.IsSelected = true);

        private void UncheckAll_Click(object sender, RoutedEventArgs e) => _rows.ForEach(r => r.IsSelected = false);

        private void CheckViews_Click(object sender, RoutedEventArgs e) => ViewModels.ForEach(v => v.IsChecked = true);

        private void UncheckViews_Click(object sender, RoutedEventArgs e) => ViewModels.ForEach(v => v.IsChecked = false);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            LevelsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "Marque pelo menos um nível.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }
}
