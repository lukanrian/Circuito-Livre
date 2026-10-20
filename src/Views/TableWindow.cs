using System;
using System.Collections.Generic;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace RafaelElectrical.Views
{
    /// <summary>Janela com uma tabela somente leitura, um resumo e botões de ação.</summary>
    public class TableWindow : Window
    {
        private readonly StackPanel _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };

        /// <summary>Texto do botão escolhido (null = fechou).</summary>
        public string Chosen { get; private set; }

        public TableWindow(string title, string summary, IList<string> headers, IEnumerable<IList<string>> rows, double width = 1000, double height = 620)
        {
            Title = title + " — RafaelElectrical";
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var table = new DataTable();
            for (int i = 0; i < headers.Count; i++) table.Columns.Add("c" + i);
            foreach (IList<string> r in rows)
            {
                DataRow row = table.NewRow();
                for (int i = 0; i < headers.Count && i < r.Count; i++) row[i] = r[i];
                table.Rows.Add(row);
            }

            var grid = new DataGrid
            {
                ItemsSource = table.DefaultView,
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                Margin = new Thickness(10, 0, 10, 0),
                HeadersVisibility = DataGridHeadersVisibility.Column,
            };

            for (int i = 0; i < headers.Count; i++)
                grid.Columns.Add(new DataGridTextColumn { Header = headers[i], Binding = new System.Windows.Data.Binding("c" + i) });

            var close = new Button { Content = "Fechar", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (_, _) => { Chosen = null; DialogResult = false; };

            var root = new DockPanel();
            var top = new TextBlock { Text = summary ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(_buttons, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(_buttons);
            root.Children.Add(grid);
            Content = root;

            Loaded += (_, _) => _buttons.Children.Add(close);
        }

        /// <summary>Botão que fecha a janela devolvendo o texto em <see cref="Chosen"/>.</summary>
        public TableWindow Action(string text, bool isDefault = false)
        {
            var b = new Button { Content = text, MinWidth = 120, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault };
            b.Click += (_, _) => { Chosen = text; DialogResult = true; };
            _buttons.Children.Add(b);
            return this;
        }
    }
}
