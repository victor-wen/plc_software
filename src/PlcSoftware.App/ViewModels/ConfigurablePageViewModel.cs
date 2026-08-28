using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcSoftware.App.Services;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.ViewModels;

/// <summary>
/// The shell-level navigation surface implemented by <c>MainWindow</c> and consumed by the
/// configurable pages: page switching, the page-list walk (上一页/下一页), history back (返回),
/// the sign-in jump and sign-out. WPF-free so the configurable page VM stays testable.
/// </summary>
public interface IConfigurableUiNavigator
{
    /// <summary>Switches to the page with the given id (no-op when unknown).</summary>
    void Navigate(string pageId);

    /// <summary>Moves to the next page of the page list.</summary>
    void NavigateDown();

    /// <summary>Moves to the previous page of the page list.</summary>
    void NavigateUp();

    /// <summary>Returns to the previously visited page.</summary>
    void NavigateBack();

    /// <summary>Switches to the sign-in page (the page that hosts the loginForm module).</summary>
    void ShowLogin();

    /// <summary>Records a successful sign-in (the shell shows the account and opens the gate).</summary>
    void SignIn(string username);

    /// <summary>Clears the shell sign-in state.</summary>
    void SignOut();
}

/// <summary>
/// One configurable operator screen (design §7: 模块化可配置界面). Wraps a <see cref="UiPageDefinition"/>
/// into WPF-free presentation state and commands: the header text, the left/right button groups
/// (<see cref="MenuButtons"/>/<see cref="NavButtons"/>), the bottom command row
/// (<see cref="CommandBarButtons"/>), the sign-in form state
/// (<see cref="Username"/>/<see cref="Password"/>/<see cref="LoginConfirmCommand"/>), the 位置参数 tables
/// (<see cref="ParameterGroups"/>) and the legacy <see cref="HostedViewName"/> passthrough.
///
/// <para><b>Action execution.</b> Every button runs its configured <see cref="UiActionDefinition"/>:
/// <c>navigate</c>/<c>login</c>/<c>logout</c>/<c>up</c>/<c>down</c>/<c>back</c> drive
/// <see cref="IConfigurableUiNavigator"/>; <c>command</c> sends the configured
/// <see cref="UiWriteDefinition"/>s through <c>ICommandService</c> (pulses are handled by the service;
/// the mode pairs 自动/直通/手动 are composed by listing both targets in one action). A transport
/// failure surfaces on <see cref="StatusText"/> and never throws.</para>
///
/// <para><b>Sign-in.</b> With no configured users the form accepts anything (simulation); otherwise the
/// credential must match an entry of <c>app.users</c>. Success runs <c>app.loginSuccess</c> (typically
/// <c>navigate</c>), failure shows a message on <see cref="LoginError"/>.</para>
///
/// <para><b>No WPF dependency.</b> Everything is plain commands + observable state; the renderer
/// (<c>ConfigurablePageView</c>) only maps this VM onto WPF controls.</para>
/// </summary>
public sealed partial class ConfigurablePageViewModel : ObservableObject
{
    private readonly UiLayoutDefinition _layout;
    private readonly UiPageDefinition _page;
    private readonly IConfigurableUiNavigator _navigator;
    private readonly ICommandService _commandService;
    private readonly ParameterService _parameterService;

    /// <summary>True while a command action is in flight (button CanExecute re-queries it).</summary>
    [ObservableProperty]
    private bool _isActionBusy;

    /// <summary>The shell status line (command outcomes, sign-in errors).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    // --- Sign-in form state -------------------------------------------------------------------------

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _loginError;

    [ObservableProperty]
    private bool _isSignedIn;

    public ConfigurablePageViewModel(
        UiLayoutDefinition layout,
        UiPageDefinition page,
        IConfigurableUiNavigator navigator,
        ICommandService commandService,
        ParameterService parameterService,
        ITileStore? tileStore = null,
        MainViewModel? mainViewModel = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _parameterService = parameterService ?? throw new ArgumentNullException(nameof(parameterService));

        MenuButtons = BuildButtons(this, page.Modules.Where(m => m.Type == UiModuleType.Menu));
        NavButtons = BuildButtons(this, page.Modules.Where(m => m.Type == UiModuleType.Nav));
        CommandBarButtons = BuildButtons(this, page.Modules.Where(m => m.Type == UiModuleType.CommandBar));
        ParameterGroups = BuildParameterGroups(page.Modules.Where(m => m.Type == UiModuleType.ParameterGroup));
        if (page.Modules.Any(m => m.Type == UiModuleType.Dashboard))
        {
            Dashboard = new DashboardViewModel(layout, page, tileStore ?? throw new ArgumentNullException(nameof(tileStore)),
                navigator, commandService, mainViewModel);
        }
    }

    /// <summary>The page id (used by the shell to key the view).</summary>
    public string PageId => _page.Id;

    /// <summary>The human-readable page title.</summary>
    public string Title => string.IsNullOrWhiteSpace(_page.Title) ? _page.Id : _page.Title;

    /// <summary>The header title text (header module override, falling back to the app title).</summary>
    public string HeaderTitle => HeaderModule is { Title.Length: > 0 } h ? h.Title : _layout.App.Title;

    /// <summary>The header logo text (header module override, falling back to the app logo).</summary>
    public string HeaderLogo => HeaderModule is { Logo.Length: > 0 } h ? h.Logo : _layout.App.Logo;

    public bool HasHeader => HeaderModule is not null;
    public bool HasMenu => MenuButtons.Count > 0;
    public bool HasNav => NavButtons.Count > 0;
    public bool HasCommandBar => CommandBarButtons.Count > 0;
    public bool HasLoginForm => _page.Modules.Any(m => m.Type == UiModuleType.LoginForm);
    public bool HasParameterGroups => ParameterGroups.Count > 0;
    public bool HasHostedView => PageHostModule is not null;

    /// <summary>The legacy page view name hosted by this configurable page (pageHost module), or null.</summary>
    public string? HostedViewName => PageHostModule?.HostedView;

    /// <summary>The left vertical button group.</summary>
    public ObservableCollection<UiButtonViewModel> MenuButtons { get; }

    /// <summary>The right vertical navigation group.</summary>
    public ObservableCollection<UiButtonViewModel> NavButtons { get; }

    /// <summary>The bottom horizontal command row.</summary>
    public ObservableCollection<UiButtonViewModel> CommandBarButtons { get; }

    /// <summary>The 位置参数 tables of the content region.</summary>
    public ObservableCollection<ParameterGroupViewModel> ParameterGroups { get; }

    /// <summary>The home dashboard board (dashboard module), or null when the page has none.</summary>
    public DashboardViewModel? Dashboard { get; }

    /// <summary>Accepts the sign-in form (validates against app.users, runs app.loginSuccess).</summary>
    /// <remarks>Only on valid credentials the shell is signed in and <c>app.loginSuccess</c> is executed —
    /// a failed login never navigates (the gate keeps the operator on the login page).</remarks>
    [RelayCommand(CanExecute = nameof(CanLoginConfirm))]
    private async Task LoginConfirmAsync(CancellationToken cancellationToken)
    {
        if (IsActionBusy)
        {
            return;
        }

        var users = _layout.App.Users;
        var accepted = users.Count == 0
            ? true // no credentials configured (simulation): accept anything.
            : users.Any(u =>
                string.Equals(u.Username, Username, StringComparison.Ordinal)
                && string.Equals(u.Password, Password, StringComparison.Ordinal));

        if (!accepted)
        {
            LoginError = "用户名或密码错误。";
            return;
        }

        IsActionBusy = true;
        try
        {
            IsSignedIn = true;
            LoginError = null;
            // Clear the password field (the view's PasswordChanged handler keeps the VM in sync).
            Password = string.Empty;
            _navigator.SignIn(Username);
            if (_layout.App.LoginSuccess is not null)
            {
                await ExecuteActionAsync(_layout.App.LoginSuccess, cancellationToken);
            }
        }
        finally
        {
            IsActionBusy = false;
        }
    }

    partial void OnIsActionBusyChanged(bool value) => LoginConfirmCommand.NotifyCanExecuteChanged();

    private bool CanLoginConfirm() => !IsActionBusy;

    /// <summary>Clears the sign-in state (also used by the shell-wide 退出登录 action).</summary>
    public void SignOut()
    {
        IsSignedIn = false;
        Username = string.Empty;
        Password = string.Empty;
        LoginError = null;
    }

    /// <summary>Executes one configured action (shared by button commands and the login-success hook).</summary>
    public async Task ExecuteActionAsync(UiActionDefinition action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case UiActionKind.Navigate:
                _navigator.Navigate(action.Page!);
                break;
            case UiActionKind.Login:
                _navigator.ShowLogin();
                break;
            case UiActionKind.Logout:
                SignOut();
                _navigator.SignOut();
                break;
            case UiActionKind.Up:
                _navigator.NavigateUp();
                break;
            case UiActionKind.Down:
                _navigator.NavigateDown();
                break;
            case UiActionKind.Back:
                _navigator.NavigateBack();
                break;
            case UiActionKind.Command:
                await SendCommandAsync(action, cancellationToken);
                break;
            case UiActionKind.None:
            default:
                break;
        }
    }

    private async Task SendCommandAsync(UiActionDefinition action, CancellationToken cancellationToken)
    {
        var wasBusy = IsActionBusy;
        if (!wasBusy)
        {
            IsActionBusy = true;
        }

        StatusText = string.Empty;
        try
        {
            foreach (var write in action.Writes)
            {
                var target = write.ResolveTarget();
                if (target is null)
                {
                    StatusText = $"未知命令目标：{write.Target}";
                    return;
                }

                var result = await _commandService.ExecuteAsync(new CommandRequest(target.Value, write.Value), cancellationToken);
                if (result.Status != CommandStatus.Success)
                {
                    StatusText = $"命令失败：{result.Message ?? result.Status.ToString()}";
                    return;
                }
            }

            StatusText = "命令已执行";
        }
        catch (OperationCanceledException)
        {
            StatusText = "命令已取消";
        }
        catch (Exception ex)
        {
            // A transport failure must never escape to a command (it would surface on the UI thread).
            StatusText = $"命令失败：{ex.Message}";
        }
        finally
        {
            if (!wasBusy)
            {
                IsActionBusy = false;
            }
        }
    }

    private static ObservableCollection<UiButtonViewModel> BuildButtons(
        ConfigurablePageViewModel owner, IEnumerable<UiModuleDefinition> modules)
    {
        var buttons = new ObservableCollection<UiButtonViewModel>();
        foreach (var module in modules)
        {
            foreach (var item in module.Buttons)
            {
                buttons.Add(new UiButtonViewModel(owner, item));
            }
        }

        return buttons;
    }

    private ObservableCollection<ParameterGroupViewModel> BuildParameterGroups(IEnumerable<UiModuleDefinition> modules)
    {
        var groups = new ObservableCollection<ParameterGroupViewModel>();
        foreach (var module in modules)
        {
            foreach (var group in module.Groups)
            {
                groups.Add(new ParameterGroupViewModel(_parameterService, group));
            }
        }

        return groups;
    }

    private UiModuleDefinition? HeaderModule
        => _page.Modules.FirstOrDefault(m => m.Type == UiModuleType.Header);

    private UiModuleDefinition? PageHostModule
        => _page.Modules.FirstOrDefault(m => m.Type == UiModuleType.PageHost);
}

/// <summary>
/// One configurable button (menu / nav / command bar). Runs its <see cref="UiActionDefinition"/>
/// through the owning page VM; the command stays enabled unless a command action is in flight.
/// </summary>
public sealed partial class UiButtonViewModel : ObservableObject
{
    private readonly ConfigurablePageViewModel _owner;
    private readonly UiActionButtonDefinition _definition;

    public UiButtonViewModel(ConfigurablePageViewModel owner, UiActionButtonDefinition definition)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>The button caption.</summary>
    public string Text => _definition.Text;

    /// <summary>Runs the configured action (commands do not throw; failures surface on the shell status line).</summary>
    [RelayCommand]
    private async Task ClickAsync(CancellationToken cancellationToken)
    {
        if (_definition.Action is not null)
        {
            await _owner.ExecuteActionAsync(_definition.Action, cancellationToken);
        }
    }
}

/// <summary>One 位置参数 table (e.g. 上料道一): caption + axis rows.</summary>
public sealed class ParameterGroupViewModel
{
    public ParameterGroupViewModel(ParameterService service, UiParameterGroupDefinition group)
    {
        Title = group.Title;
        Rows = new ObservableCollection<ParameterRowViewModel>(
            group.Rows.Select(r => new ParameterRowViewModel(service, r)));
    }

    public string Title { get; }

    public ObservableCollection<ParameterRowViewModel> Rows { get; }
}

/// <summary>One axis row: the axis caption and its position/speed fields (each writable independently).</summary>
public sealed class ParameterRowViewModel
{
    public ParameterRowViewModel(ParameterService service, UiParameterRowDefinition row)
    {
        Axis = row.Axis;
        Position = row.Position is null ? null : new ParameterFieldViewModel(service, row.Position);
        Speed = row.Speed is null ? null : new ParameterFieldViewModel(service, row.Speed);
    }

    public string Axis { get; }

    public ParameterFieldViewModel? Position { get; }

    public ParameterFieldViewModel? Speed { get; }
}

/// <summary>
/// One writable axis field (位置设定 / 速度设定): the configured register + range, the raw input and a
/// confirm write through <see cref="ParameterService"/> (write-then-verify, offline rejection, range
/// enforcement — design §5.3/§6.5). The outcome is reported on <see cref="StatusText"/>; a failed
/// write never throws.
/// </summary>
public sealed partial class ParameterFieldViewModel : ObservableObject
{
    private readonly ParameterService _service;
    private readonly UiParameterFieldDefinition _definition;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isBusy;

    public ParameterFieldViewModel(ParameterService service, UiParameterFieldDefinition definition)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>The column caption (e.g. 位置设定(M)).</summary>
    public string Label => string.IsNullOrWhiteSpace(_definition.Label) ? _definition.Register : _definition.Label;

    /// <summary>The writable parameter name (register, e.g. D201).</summary>
    public string Register => _definition.Register;

    /// <summary>The configured range hint (未配置范围 when no limits are configured).</summary>
    public string RangeHint => _definition.Min.HasValue && _definition.Max.HasValue
        ? $"{_definition.Min} ~ {_definition.Max} {_definition.Unit}"
        : "未配置范围";

    /// <summary>Writes the parsed input to <see cref="Register"/> (range-checked, write-then-verify).</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(InputText.Trim(), out var value))
        {
            StatusText = "输入不是有效整数";
            return;
        }

        if (_definition.Min.HasValue && value < _definition.Min.Value
            || _definition.Max.HasValue && value > _definition.Max.Value)
        {
            StatusText = $"超出允许范围（{RangeHint}）";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _service.WriteAsync(Register, value, cancellationToken);
            StatusText = result.Status switch
            {
                ParameterWriteStatus.Success => $"写入成功（读回一致）",
                ParameterWriteStatus.Mismatch => "写入成功但不一致，请重试",
                ParameterWriteStatus.Rejected => $"拒绝写入：{result.Message}",
                ParameterWriteStatus.Unknown => $"写入结果未知：{result.Message}",
                _ => result.Status.ToString(),
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = "写入已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"写入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConfirm() => !IsBusy;
}
