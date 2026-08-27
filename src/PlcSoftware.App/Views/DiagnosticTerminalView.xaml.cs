using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Modbus debug-terminal page (design §6.5): FC01/02/03/04 reads and FC05/06 single-point writes.
/// A pure presentation <c>UserControl</c> whose <c>DataContext</c> is the injected
/// <see cref="ViewModels.DiagnosticTerminalViewModel"/>; parsing, unlock gating, the write guard and the
/// hex/elapsed/status result presentation all live in the view model.
/// </summary>
public partial class DiagnosticTerminalView : UserControl
{
    public DiagnosticTerminalView()
    {
        InitializeComponent();
    }
}
