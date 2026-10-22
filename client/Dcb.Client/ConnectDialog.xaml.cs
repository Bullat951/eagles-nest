using System.Windows;

namespace Dcb.Client;

public partial class ConnectDialog : Window
{
    public string Host => HostBox.Text.Trim();
    public int Port { get; private set; }
    public string Password => PasswordBox.Password;
    public bool Remember => RememberBox.IsChecked == true;
    public int SrsPort { get; private set; }
    public string EamPassword => EamBox.Password;

    public ConnectDialog(string host, int port, string? password, bool remember, int srsPort, string? eamPassword)
    {
        InitializeComponent();
        HostBox.Text = host;
        PortBox.Text = port.ToString();
        PasswordBox.Password = password ?? "";
        SrsPortBox.Text = srsPort.ToString();
        EamBox.Password = eamPassword ?? "";
        RememberBox.IsChecked = remember;
        // Filled in from last time: Enter connects straight away.
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Host.Length == 0)
        {
            ErrorText.Text = "Enter a server address";
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ErrorText.Text = "Port must be 1–65535";
            return;
        }
        if (!int.TryParse(SrsPortBox.Text, out var srsPort) || srsPort is < 1 or > 65535)
        {
            ErrorText.Text = "SRS port must be 1–65535";
            return;
        }
        Port = port;
        SrsPort = srsPort;
        DialogResult = true;
    }
}
