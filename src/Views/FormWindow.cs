using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace RafaelElectrical.Views
{
    /// <summary>
    /// Formulário simples montado em código (combos, campos de texto, caixas de seleção e notas),
    /// para comandos que só precisam de algumas opções.
    /// </summary>
    public class FormWindow : Window
    {
        private readonly StackPanel _panel = new StackPanel { Margin = new Thickness(14) };
        private readonly List<Func<string>> _validators = new();

        public FormWindow(string title, string instruction, double width = 480)
        {
            Title = title + " — RafaelElectrical";
            Width = width;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            if (!string.IsNullOrWhiteSpace(instruction))
                _panel.Children.Add(new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

            var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) =>
            {
                foreach (Func<string> v in _validators)
                {
                    string error = v();
                    if (error == null) continue;
                    MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 });
            Content = root;
        }

        public FormWindow Group(string title)
        {
            _panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            return this;
        }

        public FormWindow Note(string text)
        {
            _panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 4) });
            return this;
        }

        /// <summary>Lista de opções; o item escolhido fica em <c>combo.SelectedItem</c> (o próprio objeto da lista).</summary>
        public ComboBox Combo<T>(string label, IList<T> items, Func<T, string> text, int selectedIndex = 0)
        {
            var combo = new ComboBox { MinWidth = 200 };
            foreach (T item in items)
                combo.Items.Add(new ComboBoxItem { Content = text(item), Tag = item });
            if (combo.Items.Count > 0) combo.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, combo.Items.Count - 1));
            Row(label, combo);
            return combo;
        }

        public static T Selected<T>(ComboBox combo) =>
            combo.SelectedItem is ComboBoxItem item && item.Tag is T value ? value : default;

        public TextBox Text(string label, string value)
        {
            var box = new TextBox { Text = value ?? string.Empty };
            Row(label, box);
            return box;
        }

        /// <summary>Campo numérico (aceita vírgula ou ponto). Valida entre min e max ao clicar em OK.</summary>
        public TextBox Number(string label, double value, double min, double max)
        {
            TextBox box = Text(label, value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")));
            _validators.Add(() =>
            {
                double? v = ParseNumber(box.Text);
                return v.HasValue && v.Value >= min && v.Value <= max ? null : $"\"{label.TrimEnd(':')}\" precisa ser um número entre {min} e {max}.";
            });
            return box;
        }

        public CheckBox Check(string label, bool value)
        {
            var check = new CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                IsChecked = value,
                Margin = new Thickness(0, 4, 0, 4),
            };
            _panel.Children.Add(check);
            return check;
        }

        public RadioButton Radio(string group, string label, bool value)
        {
            var radio = new RadioButton
            {
                GroupName = group,
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                IsChecked = value,
                Margin = new Thickness(0, 3, 0, 3),
            };
            _panel.Children.Add(radio);
            return radio;
        }

        public static double? ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        public static double NumberOf(TextBox box, double fallback = 0) => ParseNumber(box.Text) ?? fallback;

        private void Row(string label, UIElement field)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(field, 1);
            g.Children.Add(t);
            g.Children.Add(field);
            _panel.Children.Add(g);
        }
    }
}
