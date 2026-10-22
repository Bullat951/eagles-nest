using System.Windows.Controls;
using Dcb.Client.Map.Airfields;

namespace Dcb.Client.Panels;

/// <summary>One of the ATC tab's views as a dockable panel of its own, so several can be seen at
/// once (e.g. the diagram beside the approach, or the carrier beside its LSO view). It follows
/// the ATC pick: Diagram and Approach show the airfield picked in the ATC list, Carrier and LSO
/// the carrier; each keeps its last one while the other kind is picked.</summary>
public sealed class AtcViewPanel : UserControl
{
    public const string Diagram = "view:diagram", Approach = "view:approach", Carrier = "view:carrier", Lso = "view:lso";

    public static string TitleOf(string id) => id switch
    {
        Diagram => "Diagram",
        Approach => "Approach",
        Carrier => "Carrier",
        _ => "LSO",
    };

    private readonly string _kind;
    private readonly AirfieldDiagramView? _diagram;
    private readonly ApproachView? _approach;
    private readonly CarrierView? _carrier;
    private readonly LsoView? _lso;

    public AtcViewPanel(string kind)
    {
        _kind = kind;
        Content = kind switch
        {
            Diagram => _diagram = new AirfieldDiagramView(),
            Approach => _approach = new ApproachView(),
            Carrier => _carrier = new CarrierView(),
            _ => _lso = new LsoView(),
        };
        Loaded += (_, _) =>
        {
            AppState.AirfieldSelectionChanged += ShowPick;
            AppState.CarrierSelectionChanged += ShowPick;
            AppState.Store.AirfieldsChanged += ShowPick;
            AppState.Store.Changed += Redraw;
            AppState.Store.LsoChanged += RedrawLso;
            AppState.Queues.Changed += Redraw;
            ShowPick();
        };
        Unloaded += (_, _) =>
        {
            AppState.AirfieldSelectionChanged -= ShowPick;
            AppState.CarrierSelectionChanged -= ShowPick;
            AppState.Store.AirfieldsChanged -= ShowPick;
            AppState.Store.Changed -= Redraw;
            AppState.Store.LsoChanged -= RedrawLso;
            AppState.Queues.Changed -= Redraw;
        };
    }

    private void ShowPick()
    {
        var store = AppState.Store;
        if (_diagram is not null || _approach is not null)
        {
            if (AppState.SelectedAirfieldId is not { } id || store.Airfields.FirstOrDefault(a => a.Id == id) is not { } field) return;
            if (_diagram is not null && _diagram.Field?.Id != field.Id) _diagram.Show(field, ChartLabels.For(field));
            _approach?.Show(field);
        }
        else if (AppState.SelectedCarrierId is { } carrier)
        {
            _carrier?.Show(carrier);
            _lso?.Show(carrier);
        }
    }

    /// <summary>The picture moved on (1 s): live traffic.</summary>
    private void Redraw()
    {
        if (_lso is null) InvalidateVisualOf();
    }

    /// <summary>The fast carrier feed (10 a second): the LSO and carrier views.</summary>
    private void RedrawLso()
    {
        if (_lso is not null || _carrier is not null) InvalidateVisualOf();
    }

    private void InvalidateVisualOf() => ((System.Windows.UIElement)Content).InvalidateVisual();

    public string Kind => _kind;
}
