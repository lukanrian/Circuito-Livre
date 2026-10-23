using System.Windows;

namespace CircuitoLivre.Views
{
    public enum RenumberMode { Compact, ByName, ByLoad, ByClick, BySpaces, ByPosition }

    public enum GeoOrder { RowsLeftToRight, ColumnsTopToBottom }

    public partial class RenumberWindow : Window
    {
        public RenumberMode Mode { get; private set; }
        public GeoOrder Geo { get; private set; }
        public int StartSlot { get; private set; } = 1;

        public RenumberWindow(string panelName, int circuitCount, RenumberMode? preset = null)
        {
            InitializeComponent();
            HeaderText.Text = $"Quadro \"{panelName}\" — {circuitCount} circuito(s)";
            switch (preset)
            {
                case RenumberMode.ByName: NameRadio.IsChecked = true; break;
                case RenumberMode.ByLoad: LoadRadio.IsChecked = true; break;
                case RenumberMode.ByClick: ClickRadio.IsChecked = true; break;
                case RenumberMode.BySpaces: SpacesRadio.IsChecked = true; break;
                case RenumberMode.ByPosition: GeoRadio.IsChecked = true; break;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(StartBox.Text?.Trim(), out int start) || start < 1)
            {
                MessageBox.Show(this, "Informe uma posição inicial válida (1 ou maior).", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StartSlot = start;
            Geo = GeoCombo.SelectedIndex == 1 ? GeoOrder.ColumnsTopToBottom : GeoOrder.RowsLeftToRight;
            Mode = NameRadio.IsChecked == true ? RenumberMode.ByName
                 : LoadRadio.IsChecked == true ? RenumberMode.ByLoad
                 : ClickRadio.IsChecked == true ? RenumberMode.ByClick
                 : SpacesRadio.IsChecked == true ? RenumberMode.BySpaces
                 : GeoRadio.IsChecked == true ? RenumberMode.ByPosition
                 : RenumberMode.Compact;

            DialogResult = true;
        }
    }
}
