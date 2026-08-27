using System.Windows.Controls;

namespace PlcSoftware.App.Views;

/// <summary>
/// Parameter page (design §6.5): editable D201/D202/D204/D205 and read-only D203/D210/D212.D213.
/// A pure presentation <c>UserControl</c> whose <c>DataContext</c> is the injected
/// <see cref="ViewModels.ParametersViewModel"/>; validation, confirmation and the write/read-back flow
/// all live in the view model.
/// </summary>
public partial class ParametersView : UserControl
{
    public ParametersView()
    {
        InitializeComponent();
    }
}
