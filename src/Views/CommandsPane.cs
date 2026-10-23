using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace CircuitoLivre.Views
{
    /// <summary>Um comando do plugin como aparece no painel lateral.</summary>
    public class PaneCommand
    {
        public string Panel { get; set; }
        public string Text { get; set; }
        public string Tooltip { get; set; }
        public BitmapSource Icon { get; set; }
        /// <summary>Identificadores possíveis do botão na ribbon (o primeiro que o Revit reconhecer é usado).</summary>
        public List<string> CommandIds { get; set; } = new();
    }

    /// <summary>Executa um botão do plugin a partir do painel lateral (fora do contexto da API, via ExternalEvent).</summary>
    public class PostCommandHandler : IExternalEventHandler
    {
        public PaneCommand Pending { get; set; }

        public void Execute(UIApplication app)
        {
            PaneCommand cmd = Pending;
            Pending = null;
            if (cmd == null) return;
            foreach (string id in cmd.CommandIds)
            {
                RevitCommandId rid = RevitCommandId.LookupCommandId(id);
                if (rid == null || !app.CanPostCommand(rid)) continue;
                app.PostCommand(rid);
                return;
            }
            TaskDialog.Show("Circuito Livre", $"Não foi possível abrir \"{cmd.Text}\" pelo painel. Use o botão na aba Circuito Livre.");
        }

        public string GetName() => "Circuito Livre — painel de comandos";
    }

    /// <summary>Painel lateral (ancorável) com todos os comandos do plugin e uma busca.</summary>
    public class CommandsPane : UserControl, IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("4f6c2d0e-8a51-4c43-9d1f-2b7a7f5c1d01"));

        private readonly List<PaneCommand> _commands;
        private readonly ExternalEvent _event;
        private readonly PostCommandHandler _handler;
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(4) };
        private readonly TextBox _search = new TextBox { Margin = new Thickness(6), Height = 24 };

        public CommandsPane(List<PaneCommand> commands, ExternalEvent ev, PostCommandHandler handler)
        {
            _commands = commands;
            _event = ev;
            _handler = handler;

            var root = new DockPanel { Background = Brushes.White };
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = "Buscar comandos...", Margin = new Thickness(6, 6, 6, 0), Opacity = 0.7 });
            header.Children.Add(_search);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            _search.TextChanged += (_, _) => Fill();
            Fill();
        }

        private void Fill()
        {
            _list.Children.Clear();
            string q = (_search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var group in _commands.Where(c => q.Length == 0 || (c.Text + " " + c.Tooltip + " " + c.Panel).ToLowerInvariant().Contains(q))
                         .GroupBy(c => c.Panel))
            {
                var expander = new Expander { Header = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold }, IsExpanded = true, Margin = new Thickness(0, 2, 0, 2) };
                var wrap = new WrapPanel();
                foreach (PaneCommand c in group)
                {
                    var stack = new StackPanel { Width = 78 };
                    if (c.Icon != null) stack.Children.Add(new Image { Source = c.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 2) });
                    stack.Children.Add(new TextBlock { Text = c.Text.Replace("\n", " "), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 11 });
                    var button = new Button { Content = stack, ToolTip = c.Tooltip, Margin = new Thickness(2), Padding = new Thickness(2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                    PaneCommand cmd = c;
                    button.Click += (_, _) => { _handler.Pending = cmd; _event.Raise(); };
                    wrap.Children.Add(button);
                }
                expander.Content = wrap;
                _list.Children.Add(expander);
            }
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette,
            };
        }
    }
}
