using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Read-only overview page (design §6.2): the automatic-flow step highlight, the key safety sensors,
/// the 挡停 (stopper) state, the current/target width, the belt speed and the production count. Its
/// data context is the <see cref="ViewModels.OverviewViewModel"/> supplied by the composition root.
/// </summary>
public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }
}
