namespace AirPlayCaster;

public partial class PinWindow : System.Windows.Window
{
    public string Pin => PinBox.Text.Trim();

    public PinWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Pair_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Pin.Length < 4)
        {
            System.Windows.MessageBox.Show(this, "Enter the code shown on the Apple TV.", "Pair Apple TV");
            return;
        }
        DialogResult = true;
    }
}
