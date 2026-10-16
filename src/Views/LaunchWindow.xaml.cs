using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RafaelElectrical.Views
{
    public enum LaunchCategory { Lighting, Outlet, Switch, Telecom }

    /// <summary>Tipo de família disponível para lançamento.</summary>
    public class LaunchType
    {
        public Autodesk.Revit.DB.ElementId SymbolId { get; set; }
        public LaunchCategory Category { get; set; }
        public string Label { get; set; }
    }

    public partial class LaunchWindow : Window
    {
        private readonly List<LaunchType> _types;
        private bool _ready;

        public LaunchType Selected { get; private set; }
        public double? HeightM { get; private set; }
        public double? PowerVA { get; private set; }

        /// <summary>Alturas sugeridas por categoria (m); vazio = manter a da família.</summary>
        private static readonly Dictionary<LaunchCategory, string> DefaultHeights = new()
        {
            [LaunchCategory.Lighting] = "",
            [LaunchCategory.Outlet] = "0,30",
            [LaunchCategory.Switch] = "1,10",
            [LaunchCategory.Telecom] = "0,30",
        };

        public LaunchWindow(List<LaunchType> types)
        {
            InitializeComponent();
            _types = types;
            _ready = true;
            Refresh();
        }

        private LaunchCategory CurrentCategory =>
            OutletRadio.IsChecked == true ? LaunchCategory.Outlet
            : SwitchRadio.IsChecked == true ? LaunchCategory.Switch
            : TelecomRadio.IsChecked == true ? LaunchCategory.Telecom
            : LaunchCategory.Lighting;

        private void Category_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            HeightBox.Text = DefaultHeights[CurrentCategory];
            Refresh();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (!_ready) return;
            string q = SearchBox.Text?.Trim() ?? "";
            List<LaunchType> items = _types
                .Where(t => t.Category == CurrentCategory)
                .Where(t => q.Length == 0 || t.Label.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
            TypeList.ItemsSource = items;
            if (items.Count > 0) TypeList.SelectedIndex = 0;
            if (string.IsNullOrEmpty(HeightBox.Text)) HeightBox.Text = DefaultHeights[CurrentCategory];
        }

        private void TypeList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (TypeList.SelectedItem is not LaunchType type)
            {
                MessageBox.Show(this, "Escolha um tipo de família na lista.\nSe a lista estiver vazia, carregue as suas famílias no projeto.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryOptional(HeightBox.Text, out double? height) || height < 0 || height > 20)
            {
                MessageBox.Show(this, "Altura inválida.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!TryOptional(PowerBox.Text, out double? power) || power < 0)
            {
                MessageBox.Show(this, "Potência inválida.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Selected = type;
            HeightM = height;
            PowerVA = power;
            DialogResult = true;
        }

        private static bool TryOptional(string text, out double? value)
        {
            value = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return true;
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
            value = v;
            return true;
        }
    }
}
