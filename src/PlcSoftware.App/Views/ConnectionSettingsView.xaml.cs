using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Communication settings page (design §6.8): serial options editing, validation and a connection test. A pure
/// presentation <c>UserControl</c> whose <c>DataContext</c> is the injected
/// <see cref="ViewModels.ConnectionSettingsViewModel"/>; validation, the online lock and the test all live in
/// the view model. Production transport wiring is not involved (the demo ships on the in-memory simulation).
/// </summary>
public partial class ConnectionSettingsView : UserControl
{
    public ConnectionSettingsView()
    {
        InitializeComponent();
    }
}
