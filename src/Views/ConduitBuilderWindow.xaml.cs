using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Autodesk.Revit.DB;
using CircuitoLivre.Services;

namespace CircuitoLivre.Views
{
    /// <summary>Opções do lançamento de eletrodutos: tipos teto/parede, diâmetro, disciplina, travas de ângulo e orientação.</summary>
    public partial class ConduitBuilderWindow : Window
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly double[] CommonDiametersMm = { 16, 20, 25, 32, 40, 50, 60, 75, 85, 100 };

        public ConduitBuilderWindow(List<CheckItem> types, DimensioningSettings settings, double defaultHeightM)
        {
            InitializeComponent();
            Language = XmlLanguage.GetLanguage("pt-BR");
            Settings = settings;

            CeilingCombo.ItemsSource = types;
            WallCombo.ItemsSource = types;
            CeilingCombo.SelectedItem = Find(types, settings.ConduitCeilingTypeName) ?? types.FirstOrDefault();
            WallCombo.SelectedItem = Find(types, settings.ConduitWallTypeName) ?? CeilingCombo.SelectedItem;

            DiameterCombo.ItemsSource = CommonDiametersMm.Select(d => d.ToString("0.#", Br)).ToList();
            DiameterCombo.Text = settings.ConduitBuilderDiameterMm.ToString("0.#", Br);
            HeightBox.Text = defaultHeightM.ToString("0.00", Br);
            DisciplineBox.Text = settings.ConduitDiscipline;

            (settings.ConduitPlanAngle == ConduitAngleLock.Free ? PlanFree
                : settings.ConduitPlanAngle == ConduitAngleLock.Deg45 ? Plan45 : Plan90).IsChecked = true;
            (settings.ConduitWallAngle == ConduitAngleLock.Free ? WallFree : Wall90).IsChecked = true;
            (settings.ConduitOrientation == ConduitRunOrientation.Horizontal ? OriHorizontal
                : settings.ConduitOrientation == ConduitRunOrientation.Vertical ? OriVertical : OriDefault).IsChecked = true;
            (settings.ConduitPrioritizeCeiling ? ConnCeiling : ConnWall).IsChecked = true;
        }

        public DimensioningSettings Settings { get; }
        public ElementType CeilingType { get; private set; }
        public ElementType WallType { get; private set; }
        public double DiameterMm { get; private set; }
        public double HeightM { get; private set; }

        private static CheckItem Find(List<CheckItem> types, string name) =>
            name == null ? null : types.FirstOrDefault(t => ((ElementType)t.Tag).Name == name);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (CeilingCombo.SelectedItem is not CheckItem ceiling || WallCombo.SelectedItem is not CheckItem wall)
            {
                MessageBox.Show(this, "Escolha os tipos de teto e de parede.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse((DiameterCombo.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d <= 0)
            {
                MessageBox.Show(this, "Diâmetro inválido.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse((HeightBox.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            {
                MessageBox.Show(this, "Altura inválida.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CeilingType = (ElementType)ceiling.Tag;
            WallType = (ElementType)wall.Tag;
            DiameterMm = d;
            HeightM = h;

            Settings.ConduitCeilingTypeName = CeilingType.Name;
            Settings.ConduitWallTypeName = WallType.Name;
            Settings.ConduitBuilderDiameterMm = d;
            Settings.ConduitDiscipline = DisciplineBox.Text?.Trim() ?? "";
            Settings.ConduitPlanAngle = PlanFree.IsChecked == true ? ConduitAngleLock.Free
                : Plan45.IsChecked == true ? ConduitAngleLock.Deg45 : ConduitAngleLock.Deg90;
            Settings.ConduitWallAngle = WallFree.IsChecked == true ? ConduitAngleLock.Free : ConduitAngleLock.Deg90;
            Settings.ConduitOrientation = OriHorizontal.IsChecked == true ? ConduitRunOrientation.Horizontal
                : OriVertical.IsChecked == true ? ConduitRunOrientation.Vertical : ConduitRunOrientation.Default;
            Settings.ConduitPrioritizeCeiling = ConnCeiling.IsChecked == true;

            DialogResult = true;
        }
    }
}
