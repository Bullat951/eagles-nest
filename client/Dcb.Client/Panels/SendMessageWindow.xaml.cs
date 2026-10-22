using System.Windows;
using Dcb.Client.Core;
using Dcb.Client.Srs;

namespace Dcb.Client.Panels;

/// <summary>Sends an airfield's ATIS or information to one player of the own coalition as an
/// on-screen text box (server command "message_unit"). The text is built from the server's
/// weather and the airfield data, and can be edited before it goes.</summary>
public partial class SendMessageWindow : Window
{
    private readonly Airfield _field;

    /// <summary>A player's aircraft in the list.</summary>
    public sealed record Recipient(long UnitId, string Label);

    public SendMessageWindow(Airfield field, long? recipient, bool atis = true)
    {
        _field = field;
        InitializeComponent();
        FieldText.Text = field.Name.Replace('_', ' ');
        SecondsBox.ItemsSource = new[] { 15, 30, 60, 120 };
        SecondsBox.SelectedItem = 30;
        (atis ? AtisButton : InfoButton).IsChecked = true;
        var players = Players(field);
        PlayerBox.ItemsSource = players;
        PlayerBox.SelectedItem = players.FirstOrDefault(p => p.UnitId == recipient) ?? players.FirstOrDefault();
        if (players.Count == 0) ResultText.Text = "No player of your coalition is flying right now.";
        Compose();
    }

    /// <summary>Own coalition's aircraft with a player in them, nearest to the airfield first.</summary>
    private static List<Recipient> Players(Airfield field)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) return [];
        var (flat, flon) = theatre.ToLatLon(field.X, field.Z);
        return store.Tracks
            .Select(t => t.Unit)
            .Where(u => !u.Detected && u.Player is not null && u.Lat is not null && u.Lon is not null)
            .Select(u => (Unit: u, Nm: Nav.BearingRange(flat, flon, u.Lat!.Value, u.Lon!.Value).Meters / Nav.MetersPerNm))
            .OrderBy(x => x.Nm)
            .Select(x => new Recipient(x.Unit.Id, $"{x.Unit.Player} — {x.Unit.Callsign ?? x.Unit.Name} ({x.Unit.Type}), {x.Nm:0} NM"))
            .ToList();
    }

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && MessageText is not null) Compose();
    }

    private void Compose()
    {
        var store = AppState.Store;
        var frequencies = store.Theatre is { } t ? SrsBridge.FrequenciesOf(t.Id, _field.Id) : [];
        var wx = store.Weather?.For(_field.Id);
        if (InfoButton.IsChecked == true)
        {
            MessageText.Text = AtisText.AirfieldInfo(_field, wx, frequencies);
            return;
        }
        if (store.Weather is not { } mission || wx is null || store.Theatre is not { } theatre)
        {
            MessageText.Text = "";
            ResultText.Text = "No weather from the server yet (it comes once a minute).";
            return;
        }
        var (lat, lon) = theatre.ToLatLon(_field.X, _field.Z);
        var magvar = theatre.MagVar?.At(lat, lon) ?? 0;
        MessageText.Text = AtisText.Atis(_field, wx, mission, theatre.Id, magvar, frequencies);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerBox.SelectedItem is not Recipient to)
        {
            ResultText.Text = "Pick a player.";
            return;
        }
        var text = MessageText.Text.Trim();
        if (text.Length == 0)
        {
            ResultText.Text = "Nothing to send.";
            return;
        }
        SendButton.IsEnabled = false;
        ResultText.Text = "Sending…";
        var result = await AppState.Connection.SendCommandAsync("message_unit", new Dictionary<string, object>
        {
            ["unit"] = to.UnitId,
            ["text"] = text,
            ["seconds"] = SecondsBox.SelectedItem is int s ? s : 30,
        });
        SendButton.IsEnabled = true;
        ResultText.Text = result.Ok ? $"Shown to {to.Label.Split(" — ")[0]} ({DateTime.Now:HH:mm:ss})" : $"Not sent: {result.Error}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
