using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CircuitoLivre.Views
{
    public partial class MemorialWindow : Window
    {
        public static readonly string[] Fields =
        {
            "Construtora", "Obra", "Endereço", "Tipologia", "Características",
            "Nº de apartamentos", "Nº de lojas", "Responsável técnico", "CREA", "Cliente", "CNPJ do cliente",
        };

        private readonly Dictionary<string, TextBox> _boxes = new Dictionary<string, TextBox>();

        public Dictionary<string, string> Data => _boxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text?.Trim() ?? "");
        public List<CheckItem> Levels { get; }
        public List<CheckItem> Panels { get; }
        public List<CheckItem> Norms { get; }
        public List<CheckItem> Materials { get; }
        public bool Detailed => DetailedRadio.IsChecked == true;
        public bool IncludePerformance => PerformanceCheck.IsChecked == true;
        public bool IncludeConditions => ConditionsCheck.IsChecked == true;

        public MemorialWindow(Dictionary<string, string> prefill, List<CheckItem> levels, List<CheckItem> panels,
            List<CheckItem> norms, List<CheckItem> materials)
        {
            InitializeComponent();

            for (int i = 0; i < Fields.Length; i++)
            {
                FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = Fields[i] + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
                var box = new TextBox { Margin = new Thickness(0, 3, 0, 3), Text = prefill.TryGetValue(Fields[i], out string v) ? v : "" };
                Grid.SetRow(label, i);
                Grid.SetRow(box, i);
                Grid.SetColumn(box, 1);
                FieldsGrid.Children.Add(label);
                FieldsGrid.Children.Add(box);
                _boxes[Fields[i]] = box;
            }

            Levels = levels;
            Panels = panels;
            Norms = norms;
            Materials = materials;
            LevelsList.ItemsSource = levels;
            PanelsList.ItemsSource = panels;
            NormsList.ItemsSource = norms;
            MaterialsList.ItemsSource = materials;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
