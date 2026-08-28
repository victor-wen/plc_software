using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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
        // Root fills the content region with the deep-blue HMI background and centers the card.
        var root = new Grid
        {
            Background = Res<Brush>("ConfigUiBackgroundBrush"),
        };

        // ---- Brand header (logo + title) ---------------------------------------------------
        var logoText = string.IsNullOrWhiteSpace(vm.HeaderLogo) ? "V" : vm.HeaderLogo.Trim();
        if (logoText.Length > 4) logoText = logoText[..4];
        var appTitle = string.IsNullOrWhiteSpace(vm.HeaderTitle) ? "自动化设备" : vm.HeaderTitle;

        var logoCircle = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Background = Res<Brush>("ConfigUiAccentBrush"),
            Child = new TextBlock
            {
                Text = logoText,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var titleStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = appTitle,
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "PLC 上位机监控系统 · 欢迎登录",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        brandRow.Children.Add(logoCircle);
        brandRow.Children.Add(titleStack);

        var divider = new Border
        {
            Height = 1,
            Background = Res<Brush>("ConfigUiGridLineBrush"),
            Opacity = 0.45,
            Margin = new Thickness(0, 0, 0, 18),
        };

        var formTitle = new TextBlock
        {
            Text = "账号登录",
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var formSubtitle = new TextBlock
        {
            Text = "请输入用户名和密码",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };

        // ---- Helpers for icon fields -------------------------------------------------------
        Border MakeFieldBorder(out Grid innerGrid)
        {
            innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = Res<Brush>("ConfigUiGridLineBrush"),
                BorderThickness = new Thickness(1),
                Background = Res<Brush>("ConfigUiInputBrush"),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 12),
                Child = innerGrid,
            };
        }

        // Username field
        var userBorder = MakeFieldBorder(out var userGrid);
        var userIcon = new TextBlock
        {
            Text = "👤",
            FontSize = 14,
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var username = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            CaretBrush = Res<Brush>("ConfigUiTextBrush"),
            Padding = new Thickness(8, 10, 10, 10),
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        username.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.Username))
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        Grid.SetColumn(userIcon, 0);
        Grid.SetColumn(username, 1);
        userGrid.Children.Add(userIcon);
        userGrid.Children.Add(username);

        var userLabel = new TextBlock
        {
            Text = "用户名",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 4),
        };

        // Password field
        var pwdBorder = MakeFieldBorder(out var pwdGrid);
        pwdBorder.Margin = new Thickness(0, 0, 0, 16);
        var pwdIcon = new TextBlock
        {
            Text = "🔒",
            FontSize = 13,
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var password = new PasswordBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Res<Brush>("ConfigUiTextBrush"),
            Padding = new Thickness(8, 10, 10, 10),
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        password.PasswordChanged += (_, _) =>
        {
            vm.Password = password.Password;
            if (!string.IsNullOrEmpty(vm.LoginError))
                vm.LoginError = null;
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Password) && string.IsNullOrEmpty(vm.Password) && password.Password.Length > 0)
                password.Password = string.Empty;
        };
        Grid.SetColumn(pwdIcon, 0);
        Grid.SetColumn(password, 1);
        pwdGrid.Children.Add(pwdIcon);
        pwdGrid.Children.Add(password);

        var pwdLabel = new TextBlock
        {
            Text = "密码",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 4),
        };

        // Focus visuals + clear error on typing
        void WireFocus(Border border, Control inner)
        {
            inner.GotFocus += (_, _) => border.BorderBrush = Res<Brush>("ConfigUiAccentBrush");
            inner.LostFocus += (_, _) => border.BorderBrush = Res<Brush>("ConfigUiGridLineBrush");
        }
        WireFocus(userBorder, username);
        WireFocus(pwdBorder, password);
        username.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(vm.LoginError)) vm.LoginError = null;
        };

        // Enter to confirm
        username.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && vm.LoginConfirmCommand.CanExecute(null))
                vm.LoginConfirmCommand.Execute(null);
        };
        password.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && vm.LoginConfirmCommand.CanExecute(null))
                vm.LoginConfirmCommand.Execute(null);
        };

        // Error banner (icon + text) — collapsed when no error
        var errorIcon = new TextBlock
        {
            Text = "⚠",
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        errorText.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.LoginError)) { Source = vm });
        var errorRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        errorRow.Children.Add(errorIcon);
        errorRow.Children.Add(errorText);
        var errorBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x7A, 0x7A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 2, 0, 10),
            Visibility = string.IsNullOrEmpty(vm.LoginError) ? Visibility.Collapsed : Visibility.Visible,
            Child = errorRow,
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.LoginError))
                errorBorder.Visibility = string.IsNullOrEmpty(vm.LoginError) ? Visibility.Collapsed : Visibility.Visible;
        };

        // Confirm button + busy state
        var confirm = new Button
        {
            Content = "登 录",
            Command = vm.LoginConfirmCommand,
            Style = Res<Style>("ConfigUiModuleButtonStyle"),
            Height = 42,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDefault = true,
            Margin = new Thickness(0, 2, 0, 0),
        };
        void SyncBusy()
        {
            var busy = vm.IsActionBusy;
            confirm.Content = busy ? "登录中…" : "登 录";
            confirm.IsEnabled = !busy;
            username.IsEnabled = !busy;
            password.IsEnabled = !busy;
            userBorder.Opacity = busy ? 0.7 : 1;
            pwdBorder.Opacity = busy ? 0.7 : 1;
        }
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsActionBusy))
                SyncBusy();
        };
        SyncBusy();

        var hint = new TextBlock
        {
            Text = "默认账号  admin / 1234   ·   可在  config/ui-layout.json  配置  app.users",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 10.5,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.9,
        };
        var footer = new TextBlock
        {
            Text = "© VISA  ·  PLC 上位机监控",
            Foreground = Res<Brush>("ConfigUiMutedTextBrush"),
            FontSize = 9.5,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var form = new StackPanel
        {
            MinWidth = 300,
        };
        form.Children.Add(brandRow);
        form.Children.Add(divider);
        form.Children.Add(formTitle);
        form.Children.Add(formSubtitle);
        form.Children.Add(userLabel);
        form.Children.Add(userBorder);
        form.Children.Add(pwdLabel);
        form.Children.Add(pwdBorder);
        form.Children.Add(errorBorder);
        form.Children.Add(confirm);
        form.Children.Add(hint);
        form.Children.Add(footer);

        var card = new Border
        {
            Background = Res<Brush>("ConfigUiPanelBrush"),
            BorderBrush = Res<Brush>("ConfigUiGridLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(28, 24, 28, 22),
            MinWidth = 380,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x06, 0x1A, 0x2E),
                BlurRadius = 28,
                ShadowDepth = 12,
                Opacity = 0.5,
                Direction = 270,
            },
            Child = form,
        };

        var centered = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { card },
        };
        // Center via alignment
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        // Slight outer margin so shadow is not clipped
        card.Margin = new Thickness(24);

        // Auto-focus username after the card is loaded (defensive: can be null during shutdown/test)
        root.Loaded += (_, _) =>
        {
            try
            {
                var d = root.Dispatcher;
                if (d == null) return;
                d.BeginInvoke((Action)(() =>
                {
                    try { username.Focus(); Keyboard.Focus(username); } catch { }
                }), DispatcherPriority.Loaded);
            }
            catch { }
        };

        root.Children.Add(centered);
        return root;
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
    {
        //  ConfigUiTheme 合并在 Application 级，UserControl.Resources 找不到时要向上走到 Application 。
        //  之前直接 (T)Resources[key] 会在资源不在本地字典时抛 KeyNotFoundException，导致启动闪退。
        if (TryFindResource(key) is T found)
        {
            return found;
        }

        if (Application.Current != null)
        {
            var appRes = Application.Current.TryFindResource(key);
            if (appRes is T found2) return found2;
        }

        if (Resources.Contains(key) && Resources[key] is T found3) return found3;

        // 兜底：给常见类型一个可用的默认实例，避免调用方因 null 崩溃
        if (typeof(T) == typeof(Brush))
        {
            return (T)(object)new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xB8));
        }

        if (typeof(T) == typeof(Style))
        {
            return (T)(object)new Style();
        }

        throw new KeyNotFoundException($"Resource '{key}' not found.");
    }
}
