using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace RafaelElectrical.Views
{
    /// <summary>Item de lista com caixa de seleção.</summary>
    public class CheckItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public CheckItem(string label, object tag, bool isChecked = false)
        {
            Label = label;
            Tag = tag;
            _isChecked = isChecked;
        }

        public string Label { get; }
        public object Tag { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Janela genérica: lista com caixas de seleção + combo, texto e até duas opções opcionais.</summary>
    public partial class ChecklistWindow : Window
    {
        private readonly List<CheckItem> _items;
        private bool _requireOne = true;

        public ChecklistWindow(string title, string instruction, List<CheckItem> items, string okText = "OK")
        {
            InitializeComponent();
            Title = title + " — RafaelElectrical";
            InstructionText.Text = instruction;
            _items = items;
            ItemList.ItemsSource = items;
            OkButton.Content = okText;
        }

        public List<CheckItem> CheckedItems => _items.Where(i => i.IsChecked).ToList();
        public CheckItem ComboSelection => Combo.SelectedItem as CheckItem;
        public string Text => InputBox.Text;
        public bool Option1Checked => Option1.IsChecked == true;
        public bool Option2Checked => Option2.IsChecked == true;

        public ChecklistWindow WithCombo(string label, List<CheckItem> options, int selectedIndex = 0)
        {
            ComboRow.Visibility = Visibility.Visible;
            ComboLabel.Text = label;
            Combo.ItemsSource = options;
            if (options.Count > 0) Combo.SelectedIndex = System.Math.Min(selectedIndex, options.Count - 1);
            return this;
        }

        public ChecklistWindow WithText(string label, string value)
        {
            TextRow.Visibility = Visibility.Visible;
            TextLabel.Text = label;
            InputBox.Text = value;
            return this;
        }

        public ChecklistWindow WithOption1(string label, bool value)
        {
            Option1.Visibility = Visibility.Visible;
            Option1.Content = label;
            Option1.IsChecked = value;
            return this;
        }

        public ChecklistWindow WithOption2(string label, bool value)
        {
            Option2.Visibility = Visibility.Visible;
            Option2.Content = label;
            Option2.IsChecked = value;
            return this;
        }

        public ChecklistWindow AllowEmpty()
        {
            _requireOne = false;
            return this;
        }

        private void CheckAll_Click(object sender, RoutedEventArgs e) => _items.ForEach(i => i.IsChecked = true);

        private void UncheckAll_Click(object sender, RoutedEventArgs e) => _items.ForEach(i => i.IsChecked = false);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_requireOne && _items.Count > 0 && CheckedItems.Count == 0)
            {
                MessageBox.Show(this, "Marque pelo menos um item.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }
}
