using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PlcSoftware.App.ViewModels;

namespace PlcSoftware.App.Views;

/// <summary>
/// Renders one configurable operator screen (design §7: 模块化可配置界面) from its
/// <see cref="ConfigurablePageViewModel"/>. The five fixed HMI regions declared in the XAML shell
/// (header / left menu / content / right nav / bottom command row) are filled from the page's modules:
///
/// <list type="bullet">
///   <item><c>header</c> → the title bar (logo + title).</item>
///   <item><c>menu</c> → the vertical left button group.</item>
///   <item><c>nav</c> → the vertical right navigation group.</item>
///   <item><c>commandBar</c> → the horizontal bottom command row.</item>
///   <item><c>loginForm</c> → the sign-in card (username / password / confirm).</item>
///   <item><c>parameterGroup</c> → one card per 位置参数 table (axis rows × position/speed fields).</item>
///   <item><c>pageHost</c> → the legacy page view supplied by the shell (set via <see cref="SetHostedContent"/>).</item>
/// </list>
///
/// <para>The view is rebuilt whenever the page changes (<see cref="Apply"/>); it owns no VM state.</para>
/// </summary>
public partial class ConfigurablePageView : UserControl
{
    public ConfigurablePageView()
    {
        InitializeComponent();
    }

    /// <summary>The legacy page content hosted by the pageHost module (resolved by the shell), or null.</summary>
    public FrameworkElement? HostedContent { get; private set; }

    /// <summary>Rebuilds all five regions from <paramref name="viewModel"/>.</summary>
    public void Apply(ConfigurablePageViewModel viewModel)
    {
        HeaderHost.Content = viewModel.HasHeader ? BuildHeader(viewModel) : null;
        MenuHost.Content = viewModel.HasMenu ? BuildButtonColumn(viewModel.MenuButtons, vertical: true) : null;
        NavHost.Content = viewModel.HasNav ? BuildButtonColumn(viewModel.NavButtons, vertical: true) : null;
        CommandBarHost.Content = viewModel.HasCommandBar ? BuildButtonColumn(viewModel.CommandBarButtons, vertical: false) : null;
        ContentHost.Content = BuildContent(viewModel);
    }

    /// <summary>Sets the legacy page content for the pageHost module (called by the shell before Apply).</summary>
    public void SetHostedContent(FrameworkElement? content) => HostedContent = content;

    private UIElement BuildHeader(ConfigurablePageViewModel vm)
    {
        var logo = new TextBlock
        {
            Text = vm.HeaderLogo,
            Foreground = Res<Brush>("ConfigUiAccentBrush"),
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = vm.HeaderTitle,
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new DockPanel
        {
            LastChildFill = true,
        };
        panel.Children.Add(logo);
        panel.Children.Add(title);
        return new Border
        {
            Background = Res<Brush>("ConfigUiHeaderBrush"),
            BorderBrush = Res<Brush>("ConfigUiAccentBrush"),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Child = panel,
            Padding = new Thickness(6, 6, 6, 6),
        };
    }

    private UIElement BuildButtonColumn(IEnumerable<UiButtonViewModel> buttons, bool vertical)
    {
        var panel = vertical
            ? (Panel)new StackPanel { VerticalAlignment = VerticalAlignment.Center }
            : new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var button in buttons)
        {
            panel.Children.Add(new Button
            {
                Content = button.Text,
                Command = button.ClickCommand,
                Style = Res<Style>("ConfigUiModuleButtonStyle"),
            });
        }

        return panel;
    }

    private UIElement BuildContent(ConfigurablePageViewModel vm)
    {
        if (vm.HasLoginForm)
        {
            return BuildLoginForm(vm);
        }

        if (vm.HasParameterGroups)
        {
            return BuildParameterGroups(vm);
        }

        if (vm.Dashboard is not null)
        {
            var dashboardView = new DashboardView();
            dashboardView.Apply(vm.Dashboard);
            return dashboardView;
        }

        if (vm.HasHostedView && HostedContent is not null)
        {
            return HostedContent;
        }

        return new TextBlock
        {
            Text = "（此页面没有可显示的内容——在 config/ui-layout.json 中添加模块。）",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private UIElement BuildLoginForm(ConfigurablePageViewModel vm)
    {
        // Title
        var title = new TextBlock
        {
            Text = "用户登录",
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var subtitle = new TextBlock
        {
            Text = "请输入账号和密码",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        };

        var usernameLabel = new TextBlock
        {
            Text = "用户名",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var username = new TextBox
        {
            Style = Res<Style>("ConfigUiInputStyle"),
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 14),
        };
        username.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.Username))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

        var passwordLabel = new TextBlock
        {
            Text = "密码",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var password = new PasswordBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 16),
        };
        password.PasswordChanged += (_, _) =>
        {
            vm.Password = password.Password;
            if (!string.IsNullOrEmpty(vm.LoginError))
            {
                vm.LoginError = null;
            }
        };
        // Keep box in sync when VM clears password after success; needs to be added after
        // the handler above so the vm.Password reset does not re-enter the first handler incorrectly.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Password) && string.IsNullOrEmpty(vm.Password) && password.Password.Length > 0)
            {
                password.Password = string.Empty;
            }
        };
        // Clear stale error as soon as the user types again.
        username.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(vm.LoginError))
            {
                vm.LoginError = null;
            }
        };

        var confirm = new Button
        {
            Content = "登 录",
            Command = vm.LoginConfirmCommand,
            Style = Res<Style>("ConfigUiModuleButtonStyle"),
            Width = 280,
            Height = 38,
            FontSize = 15,
            IsDefault = true,
        };
        // Enter on either field confirms.
        username.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && vm.LoginConfirmCommand.CanExecute(null))
            {
                vm.LoginConfirmCommand.Execute(null);
            }
        };
        password.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && vm.LoginConfirmCommand.CanExecute(null))
            {
                vm.LoginConfirmCommand.Execute(null);
            }
        };

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinHeight = 18,
            Visibility = string.IsNullOrEmpty(vm.LoginError) ? Visibility.Collapsed : Visibility.Visible,
        };
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.LoginError)) { Source = vm });
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.LoginError))
            {
                error.Visibility = string.IsNullOrEmpty(vm.LoginError) ? Visibility.Collapsed : Visibility.Visible;
            }
        };

        var hint = new TextBlock
        {
            Text = "默认账号 admin / 1234  （可在 config/ui-layout.json 中配置 app.users）",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.85,
        };

        var form = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 280,
        };
        form.Children.Add(title);
        form.Children.Add(subtitle);
        form.Children.Add(usernameLabel);
        form.Children.Add(username);
        form.Children.Add(passwordLabel);
        form.Children.Add(password);
        form.Children.Add(confirm);
        form.Children.Add(error);
        form.Children.Add(hint);

        var card = new Border
        {
            Style = Res<Style>("ConfigUiCardStyle"),
            Child = form,
            Padding = new Thickness(32, 28, 32, 28),
            MinWidth = 360,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Center the card on the deep-blue background.
        return new Grid
        {
            Background = Brushes.Transparent,
            Children = { card },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private UIElement BuildParameterGroups(ConfigurablePageViewModel vm)
    {
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var group in vm.ParameterGroups)
        {
            wrap.Children.Add(BuildParameterGroup(group));
        }

        return wrap;
    }

    private UIElement BuildParameterGroup(ParameterGroupViewModel group)
    {
        var grid = new Grid { Margin = new Thickness(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // axis
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); // position input
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // position confirm
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); // speed input
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // speed confirm

        // Caption row.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock
        {
            Text = group.Title,
            Foreground = Res<Brush>("ConfigUiAccentBrush"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 4),
        };
        Grid.SetRow(caption, 0);
        Grid.SetColumnSpan(caption, 5);
        grid.Children.Add(caption);

        void AddRow(int row, string axisCaption, ParameterFieldViewModel? position, ParameterFieldViewModel? speed)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var axis = new TextBlock
            {
                Text = axisCaption,
                Foreground = Res<Brush>("ConfigUiTextBrush"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 8, 2),
            };
            Grid.SetRow(axis, row);
            Grid.SetColumn(axis, 0);
            grid.Children.Add(axis);

            AddField(row, 1, position);
            AddField(row, 3, speed);
        }

        void AddField(int row, int column, ParameterFieldViewModel? field)
        {
            if (field is null)
            {
                return;
            }

            var input = new TextBox { Style = Res<Style>("ConfigUiInputStyle"), FontSize = 13 };
            input.SetBinding(TextBox.TextProperty, new Binding(nameof(ParameterFieldViewModel.InputText))
            {
                Source = field,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            Grid.SetRow(input, row);
            Grid.SetColumn(input, column);
            grid.Children.Add(input);

            var confirm = new Button
            {
                Content = "确定",
                Command = field.ConfirmCommand,
                Style = Res<Style>("ConfigUiModuleButtonStyle"),
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
            };
            Grid.SetRow(confirm, row);
            Grid.SetColumn(confirm, column + 1);
            grid.Children.Add(confirm);
        }

        var row = 1;
        foreach (var axisRow in group.Rows)
        {
            AddRow(row++, axisRow.Axis, axisRow.Position, axisRow.Speed);
        }

        return new Border { Style = Res<Style>("ConfigUiCardStyle"), Child = grid };
    }

    private T Res<T>(string key) where T : class
        => (T)Resources[key];
}
