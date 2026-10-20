using System.Windows;
using System.Windows.Controls;
using RafaelElectrical.Services;

namespace RafaelElectrical.Views
{
    public partial class IdentifyWindow : Window
    {
        public string Pattern { get; private set; }
        public bool AllPanels { get; private set; }
        public bool IncludeDevices { get; private set; }
        public bool FillLevel { get; private set; }
        public bool AutoUpdate { get; private set; }

        public IdentifyWindow(AutoIdOptions saved = null)
        {
            InitializeComponent();
            if (saved != null)
            {
                if (!string.IsNullOrWhiteSpace(saved.Pattern)) PatternBox.Text = saved.Pattern;
                DevicesCheck.IsChecked = saved.IncludeDevices;
                LevelCheck.IsChecked = saved.FillLevel;
                AutoCheck.IsChecked = saved.Enabled;
            }
            UpdatePreview();
        }

        private void PatternBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            if (PreviewText == null) return;
            PreviewText.Text = "Exemplo: " + IdentificationFormatter.Format(PatternBox.Text, "QD1", "5", "Tomadas sala", "Térreo");
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PatternBox.Text))
            {
                MessageBox.Show(this, "Informe o padrão da identificação.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Pattern = PatternBox.Text;
            AllPanels = AllRadio.IsChecked == true;
            IncludeDevices = DevicesCheck.IsChecked == true;
            FillLevel = LevelCheck.IsChecked == true;
            AutoUpdate = AutoCheck.IsChecked == true;
            DialogResult = true;
        }
    }
}
