using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CircuitoLivre.Commands;
using CircuitoLivre.Selection;
using CircuitoLivre.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CircuitoLivre.Views
{
    /// <summary>
    /// Ações do painel modeless, executadas no contexto da API via ExternalEvent:
    /// recarregar tipos do documento, adicionar pontos (seleção) e construir a rota acumulada.
    /// </summary>
    public class ConduitBuilderHandler : IExternalEventHandler
    {
        public enum Op { RefreshTypes, AddPoints, Build }

        private readonly ConduitBuilderPane _pane;
        public Op Pending { get; set; }

        public ConduitBuilderHandler(ConduitBuilderPane pane) => _pane = pane;

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null || uidoc.Document.IsFamilyDocument) { _pane.SetStatus("Abra um projeto para usar o painel."); return; }
            Document doc = uidoc.Document;
            switch (Pending)
            {
                case Op.RefreshTypes: RefreshTypes(doc); break;
                case Op.AddPoints: AddPoints(uidoc); break;
                case Op.Build: Build(uidoc); break;
            }
        }

        private void RefreshTypes(Document doc)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ElementType>().Select(t => "Eletroduto: " + t.Name)
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<ElementType>().Select(t => "Eletrocalha: " + t.Name))
                .ToList();
            _pane.SetTypes(names);
            _pane.SetStatus(names.Count == 0 ? "Nenhum tipo de eletroduto/eletrocalha no projeto." : $"{names.Count} tipo(s) carregado(s).");
        }

        private void AddPoints(UIDocument uidoc)
        {
            bool wallTray = _pane.WallTypeLabel?.StartsWith("Eletrocalha") == true;
            try
            {
                IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, "Selecione os componentes e pressione Concluir");
                foreach (Reference r in refs)
                    BuildInfrastructureCommand.AddPoint(uidoc.Document.GetElement(r), wallTray, _pane.Points, _pane.Plugs);
                _pane.SetStatus($"{_pane.Points.Count} ponto(s) no caminho. Clique em Construir (Ctrl+Space).");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
        }

        private void Build(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            if (doc.ActiveView is not ViewPlan plan || plan.GenLevel == null) { _pane.SetStatus("Abra uma planta para construir."); return; }
            if (_pane.Points.Count < 2) { _pane.SetStatus("São necessários pelo menos dois pontos no caminho."); return; }
            ElementType ceiling = ByLabel(doc, _pane.CeilingTypeLabel), wall = ByLabel(doc, _pane.WallTypeLabel);
            if (ceiling == null || wall == null) { _pane.SetStatus("Escolha os tipos de teto e parede (Atualizar tipos)."); return; }
            if (!_pane.TryReadDimensions(out double diameterMm, out double heightM, out string err)) { _pane.SetStatus(err); return; }

            DimensioningSettings s = DimensioningSettings.Load();
            s.ConduitBuilderDiameterMm = diameterMm;
            s.ConduitDiscipline = _pane.Discipline;
            s.ConduitPlanAngle = _pane.PlanAngle;
            s.ConduitWallAngle = _pane.WallAngle;
            s.ConduitOrientation = _pane.Orientation;
            s.ConduitPrioritizeCeiling = _pane.PreferCeiling;
            s.Save();

            var routes = new List<(List<XYZ> pts, List<Connector> plg)> { (_pane.Points.ToList(), _pane.Plugs.ToList()) };
            string summary = BuildInfrastructureCommand.RunBuild(doc, plan.GenLevel, heightM, ceiling, wall, s, routes);
            _pane.ClearPath();
            _pane.SetStatus(summary);
        }

        private static ElementType ByLabel(Document doc, string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            string name = label.Contains(": ") ? label[(label.IndexOf(": ", StringComparison.Ordinal) + 2)..] : label;
            return new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .FirstOrDefault(t => (t is ConduitType || t is CableTrayType) && t.Name == name);
        }

        public string GetName() => "Circuito Livre — lançar eletrodutos";
    }

    /// <summary>Painel lateral (modeless) para lançar eletrodutos: tipos, ângulos, acúmulo de caminho e construção.</summary>
    public class ConduitBuilderPane : UserControl, IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("7b3e1a44-2d9c-4f1a-9c22-9a1f6e0b55aa"));

        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly double[] CommonDiametersMm = { 16, 20, 25, 32, 40, 50, 60, 75, 85, 100 };

        private ExternalEvent _event;
        private ConduitBuilderHandler _handler;

        private readonly ComboBox _ceiling = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _wall = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _diameter = new() { IsEditable = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _height = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _discipline = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _planAngle = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _wallAngle = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _orientation = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly ComboBox _connection = new() { Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold };

        public List<XYZ> Points { get; } = new();
        public List<Connector> Plugs { get; } = new();

        public ConduitBuilderPane()
        {
            var settings = DimensioningSettings.Load();
            var root = new StackPanel { Margin = new Thickness(8), Background = Brushes.White };

            root.Children.Add(Label("Eletroduto do teto/laje"));
            root.Children.Add(_ceiling);
            root.Children.Add(Label("Eletroduto da parede"));
            root.Children.Add(_wall);
            root.Children.Add(Label("Diâmetro (mm) / Altura do teto (m)"));
            var dim = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            dim.Children.Add(_diameter);
            dim.Children.Add(new TextBlock { Text = "  /  ", VerticalAlignment = VerticalAlignment.Center });
            dim.Children.Add(_height);
            root.Children.Add(dim);
            root.Children.Add(Label("Disciplina / serviço"));
            root.Children.Add(_discipline);

            root.Children.Add(Label("Ângulo em planta"));
            Fill(_planAngle, new[] { "Livre", "90°", "45°" });
            root.Children.Add(_planAngle);
            root.Children.Add(Label("Ângulo na parede"));
            Fill(_wallAngle, new[] { "Livre", "90°" });
            root.Children.Add(_wallAngle);
            root.Children.Add(Label("Orientação do L"));
            Fill(_orientation, new[] { "Padrão", "Horizontal", "Vertical" });
            root.Children.Add(_orientation);
            root.Children.Add(Label("Modo de conexão"));
            Fill(_connection, new[] { "Priorizar teto", "Mesma parede" });
            root.Children.Add(_connection);

            _diameter.ItemsSource = CommonDiametersMm.Select(d => d.ToString("0.#", Br)).ToList();
            _diameter.Text = settings.ConduitBuilderDiameterMm.ToString("0.#", Br);
            _height.Text = settings.InfraDefaultHeightM.ToString("0.00", Br);
            _discipline.Text = settings.ConduitDiscipline;
            _planAngle.SelectedIndex = settings.ConduitPlanAngle == ConduitAngleLock.Free ? 0 : settings.ConduitPlanAngle == ConduitAngleLock.Deg45 ? 2 : 1;
            _wallAngle.SelectedIndex = settings.ConduitWallAngle == ConduitAngleLock.Free ? 0 : 1;
            _orientation.SelectedIndex = settings.ConduitOrientation == ConduitRunOrientation.Horizontal ? 1 : settings.ConduitOrientation == ConduitRunOrientation.Vertical ? 2 : 0;
            _connection.SelectedIndex = settings.ConduitPrioritizeCeiling ? 0 : 1;

            var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(MakeButton("Atualizar tipos", ConduitBuilderHandler.Op.RefreshTypes));
            buttons.Children.Add(MakeButton("Adicionar pontos", ConduitBuilderHandler.Op.AddPoints));
            var clear = new Button { Content = "Apagar caminho", Margin = new Thickness(2), Padding = new Thickness(2) };
            clear.Click += (_, _) => { ClearPath(); SetStatus("Caminho apagado."); };
            buttons.Children.Add(clear);
            buttons.Children.Add(MakeButton("Construir (Ctrl+Space)", ConduitBuilderHandler.Op.Build));
            root.Children.Add(buttons);
            root.Children.Add(_status);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            // Ctrl+Space = construir
            InputBindings.Add(new KeyBinding(new RelayCommand(() => Raise(ConduitBuilderHandler.Op.Build)), Key.Space, ModifierKeys.Control));
            SetStatus("Clique em Atualizar tipos e depois em Adicionar pontos.");
        }

        /// <summary>Liga o painel ao ExternalEvent (chamado no registro em App).</summary>
        public void Wire(ExternalEvent ev, ConduitBuilderHandler handler) { _event = ev; _handler = handler; }

        private Button MakeButton(string text, ConduitBuilderHandler.Op op)
        {
            var b = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(2) };
            b.Click += (_, _) => Raise(op);
            return b;
        }

        private void Raise(ConduitBuilderHandler.Op op)
        {
            if (_event == null || _handler == null) return;
            _handler.Pending = op;
            _event.Raise();
        }

        private static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 1) };
        private static void Fill(ComboBox c, string[] items) { foreach (string i in items) c.Items.Add(i); c.SelectedIndex = 0; }

        public string CeilingTypeLabel => _ceiling.SelectedItem as string;
        public string WallTypeLabel => _wall.SelectedItem as string;
        public string Discipline => (_discipline.Text ?? "").Trim();
        public ConduitAngleLock PlanAngle => _planAngle.SelectedIndex == 0 ? ConduitAngleLock.Free : _planAngle.SelectedIndex == 2 ? ConduitAngleLock.Deg45 : ConduitAngleLock.Deg90;
        public ConduitAngleLock WallAngle => _wallAngle.SelectedIndex == 0 ? ConduitAngleLock.Free : ConduitAngleLock.Deg90;
        public ConduitRunOrientation Orientation => _orientation.SelectedIndex == 1 ? ConduitRunOrientation.Horizontal : _orientation.SelectedIndex == 2 ? ConduitRunOrientation.Vertical : ConduitRunOrientation.Default;
        public bool PreferCeiling => _connection.SelectedIndex == 0;

        public bool TryReadDimensions(out double diameterMm, out double heightM, out string error)
        {
            error = null; heightM = 0;
            if (!double.TryParse((_diameter.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out diameterMm) || diameterMm <= 0)
            { error = "Diâmetro inválido."; return false; }
            if (!double.TryParse((_height.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out heightM))
            { error = "Altura inválida."; return false; }
            return true;
        }

        public void SetTypes(List<string> labels)
        {
            string ceil = CeilingTypeLabel, wal = WallTypeLabel;
            _ceiling.ItemsSource = labels; _wall.ItemsSource = labels;
            _ceiling.SelectedItem = labels.Contains(ceil) ? ceil : labels.FirstOrDefault();
            _wall.SelectedItem = labels.Contains(wal) ? wal : _ceiling.SelectedItem;
        }

        public void ClearPath() { Points.Clear(); Plugs.Clear(); }
        public void SetStatus(string text) => Dispatcher.Invoke(() => _status.Text = text);

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette,
            };
        }

        /// <summary>Comando WPF simples para o atalho de teclado.</summary>
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _run;
            public RelayCommand(Action run) => _run = run;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _run();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
