using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// I/O diagnostics page (design §6.6): the X/Y/M grouped read-only table. A pure presentation
/// <c>UserControl</c> whose <c>DataContext</c> is the injected <see cref="ViewModels.IoDiagnosticsViewModel"/>;
/// the point-map-driven grouping and snapshot matching all live in the view model. There is deliberately no
/// write entry here (Gate 7) — manual actions run through the manual page.
/// </summary>
public partial class IoDiagnosticsView : UserControl
{
    public IoDiagnosticsView()
    {
        InitializeComponent();
    }
}
